using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Transactions;

internal sealed record WindowsInstallerOwnedDirectory(InstallerDirectoryRole Role, WindowsFileIdentity Identity);

/// <summary>Only a positively observed directory creation can add an object to this ledger.</summary>
internal sealed class WindowsInstallerDirectoryLedger
{
    internal WindowsInstallerDirectoryLedger(long generation,
        IEnumerable<WindowsInstallerOwnedDirectory> directories, InstallerTransactionSnapshot? terminal)
    {
        ArgumentNullException.ThrowIfNull(directories);
        Generation = generation;
        Directories = Array.AsReadOnly(directories.ToArray());
        Terminal = terminal;
        Validate();
    }

    internal long Generation { get; }
    internal ReadOnlyCollection<WindowsInstallerOwnedDirectory> Directories { get; }
    internal InstallerTransactionSnapshot? Terminal { get; }
    internal static WindowsInstallerDirectoryLedger Empty => new(0, [], null);

    internal WindowsInstallerDirectoryLedger RecordCreated(InstallerDirectoryRole role, WindowsFileIdentity identity)
    {
        var entry = new WindowsInstallerOwnedDirectory(role, identity);
        if (Directories.Contains(entry)) { return this; }
        return new(checked(Generation + 1), Directories.Where(item => item.Role != role)
            .Append(entry).OrderBy(item => item.Role), Terminal);
    }

    internal WindowsInstallerDirectoryLedger BeginTerminal(InstallerTransactionSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Terminal is not null)
        {
            if (Terminal != state) { throw Failure("terminal_identity_mismatch"); }
            return this;
        }
        return new(checked(Generation + 1), Directories, state);
    }

    internal void Validate()
    {
        if (Generation < 0 || Directories.Count > 6) { throw Failure("shape_invalid"); }
        InstallerDirectoryRole? previous = null;
        foreach (WindowsInstallerOwnedDirectory entry in Directories)
        {
            if (entry is null || !Enum.IsDefined(entry.Role) || entry.Identity.FileIndex == 0
                || previous is { } last && entry.Role <= last) { throw Failure("directory_identity_invalid"); }
            previous = entry.Role;
        }
        if (Terminal is { } state)
        {
            state.Validate();
            if (state.Journal.Operation != InstallerOperation.Uninstall
                || state.Journal.Phase != InstallerTransactionPhase.Verified)
            {
                throw Failure("terminal_not_verified_uninstall");
            }
        }
    }

    internal static InstallerProtocolException Failure(string suffix) => new("installer.directory_ledger." + suffix);
}

internal static class WindowsInstallerDirectoryLedgerCodec
{
    internal const int MaximumDocumentBytes = 4096;

    internal static byte[] Serialize(WindowsInstallerDirectoryLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ledger.Validate();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteNumber("generation", ledger.Generation);
            writer.WriteStartArray("createdDirectories");
            foreach (WindowsInstallerOwnedDirectory directory in ledger.Directories)
            {
                writer.WriteStartObject();
                writer.WriteString("role", directory.Role.ToString());
                writer.WriteNumber("volumeSerialNumber", directory.Identity.VolumeSerialNumber);
                writer.WriteNumber("fileIndex", directory.Identity.FileIndex);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (ledger.Terminal is not { } terminal) { writer.WriteNull("terminal"); }
            else
            {
                writer.WriteStartObject("terminal");
                writer.WriteString("journalBase64", Convert.ToBase64String(InstallerTransactionCodec.Serialize(terminal.Journal)));
                writer.WriteString("contentHash", terminal.ContentHash);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        byte[] bytes = buffer.WrittenSpan.ToArray();
        ValidateSize(bytes);
        return bytes;
    }

    internal static WindowsInstallerDirectoryLedger Parse(ReadOnlyMemory<byte> bytes)
    {
        ValidateSize(bytes.Span);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
            });
            JsonElement root = document.RootElement;
            ExactObject(root, "schema", "generation", "createdDirectories", "terminal");
            if (root.GetProperty("schema").GetInt32() != 1) { throw new JsonException(); }
            var directories = new List<WindowsInstallerOwnedDirectory>();
            foreach (JsonElement entry in root.GetProperty("createdDirectories").EnumerateArray())
            {
                ExactObject(entry, "role", "volumeSerialNumber", "fileIndex");
                string roleText = entry.GetProperty("role").GetString() ?? throw new JsonException();
                if (!Enum.TryParse(roleText, ignoreCase: false, out InstallerDirectoryRole role)
                    || !Enum.IsDefined(role) || role.ToString() != roleText) { throw new JsonException(); }
                directories.Add(new(role, new(entry.GetProperty("volumeSerialNumber").GetUInt32(),
                    entry.GetProperty("fileIndex").GetUInt64())));
                if (directories.Count > 6) { throw new JsonException(); }
            }
            InstallerTransactionSnapshot? terminal = null;
            JsonElement terminalElement = root.GetProperty("terminal");
            if (terminalElement.ValueKind != JsonValueKind.Null)
            {
                ExactObject(terminalElement, "journalBase64", "contentHash");
                string encoded = terminalElement.GetProperty("journalBase64").GetString() ?? throw new JsonException();
                byte[] journalBytes = Convert.FromBase64String(encoded);
                if (Convert.ToBase64String(journalBytes) != encoded) { throw new JsonException(); }
                InstallerTransactionJournal journal = InstallerTransactionCodec.Parse(journalBytes);
                byte[] canonical = InstallerTransactionCodec.Serialize(journal);
                string contentHash = terminalElement.GetProperty("contentHash").GetString() ?? throw new JsonException();
                if (!journalBytes.AsSpan().SequenceEqual(canonical)
                    || Convert.ToHexStringLower(SHA256.HashData(canonical)) != contentHash) { throw new JsonException(); }
                terminal = new(journal, contentHash);
            }
            var ledger = new WindowsInstallerDirectoryLedger(root.GetProperty("generation").GetInt64(), directories, terminal);
            if (!bytes.Span.SequenceEqual(Serialize(ledger)))
            {
                throw WindowsInstallerDirectoryLedger.Failure("document_noncanonical");
            }
            return ledger;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InstallerProtocolException("installer.directory_ledger.document_invalid", exception);
        }
    }

    private static void ExactObject(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name) || !properties.Contains(property.Name, StringComparer.Ordinal)) { throw new JsonException(); }
        }
        if (names.Count != properties.Length) { throw new JsonException(); }
    }

    private static void ValidateSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumDocumentBytes) { throw WindowsInstallerDirectoryLedger.Failure("document_size_invalid"); }
    }
}
