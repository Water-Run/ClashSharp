using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Machines;

/// <summary>Strict bounded JSON codec for commands on one helper session.</summary>
public static class InstallerMachineHelperCommandCodec
{
    /// <summary>Maximum encoded helper command accepted over local IPC.</summary>
    public const int MaximumCommandBytes = 4 * 1024;

    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "schema",
        "verb",
        "transactionId",
        "journalContentHash",
        "journalBase64",
    };
    /// <summary>Serializes one validated command to compact UTF-8 without a BOM.</summary>
    public static byte[] Serialize(InstallerMachineHelperCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", command.Schema);
            writer.WriteString("verb", VerbText(command.Verb));
            writer.WriteString("transactionId", command.TransactionId);
            writer.WriteString("journalContentHash", command.JournalContentHash);
            writer.WriteString("journalBase64", command.JournalBase64);
            writer.WriteEndObject();
        }

        byte[] bytes = buffer.WrittenSpan.ToArray();
        ValidateSize(bytes);
        return bytes;
    }

    /// <summary>Parses an exact command without accepting unknown or duplicate fields.</summary>
    public static InstallerMachineHelperCommand Parse(ReadOnlySpan<byte> bytes)
    {
        ValidateSize(bytes);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The helper command must be an object.");
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!RequiredProperties.Contains(property.Name)
                    || !observed.Add(property.Name))
                {
                    throw new JsonException(
                        "The helper command has an unknown or duplicate property.");
                }

                bool validType = property.Name switch
                {
                    "schema" => property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out _),
                    "verb" or "transactionId" or "journalContentHash" or "journalBase64" =>
                        property.Value.ValueKind == JsonValueKind.String,
                    _ => false,
                };
                if (!validType)
                {
                    throw new JsonException("A helper command property has an invalid type.");
                }
            }

            if (!observed.SetEquals(RequiredProperties))
            {
                throw new JsonException("The helper command property set is incomplete.");
            }

            InstallerMachineHelperCommand command = new(
                root.GetProperty("schema").GetInt32(),
                ParseVerb(root.GetProperty("verb").GetString()),
                root.GetProperty("transactionId").GetString()
                    ?? throw new JsonException("The helper transaction is null."),
                root.GetProperty("journalContentHash").GetString()
                    ?? throw new JsonException("The helper journal hash is null."),
                root.GetProperty("journalBase64").GetString()
                    ?? throw new JsonException("The helper journal payload is null."));
            command.Validate();
            byte[] canonical = Serialize(command);
            if (!CryptographicOperations.FixedTimeEquals(bytes, canonical))
            {
                throw new JsonException("The helper command is not canonical UTF-8 JSON.");
            }

            return command;
        }
        catch (JsonException exception)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.command_json_invalid",
                exception);
        }
    }

    private static void ValidateSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumCommandBytes)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.command_size_invalid");
        }
    }

    private static string VerbText(InstallerMachineHelperVerb verb) => verb switch
    {
        InstallerMachineHelperVerb.Prepare => "prepare",
        InstallerMachineHelperVerb.CommitPackage => "commit-package",
        InstallerMachineHelperVerb.Apply => "apply",
        InstallerMachineHelperVerb.Remove => "remove",
        InstallerMachineHelperVerb.Verify => "verify",
        _ => throw new InstallerProtocolException(
            "installer.machine_helper.verb_invalid"),
    };

    private static InstallerMachineHelperVerb ParseVerb(string? value) => value switch
    {
        "prepare" => InstallerMachineHelperVerb.Prepare,
        "commit-package" => InstallerMachineHelperVerb.CommitPackage,
        "apply" => InstallerMachineHelperVerb.Apply,
        "remove" => InstallerMachineHelperVerb.Remove,
        "verify" => InstallerMachineHelperVerb.Verify,
        _ => throw new JsonException("The helper command verb is invalid."),
    };
}
