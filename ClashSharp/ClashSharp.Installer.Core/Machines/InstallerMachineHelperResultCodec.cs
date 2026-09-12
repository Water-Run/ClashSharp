using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Machines;

/// <summary>Strict bounded JSON codec for elevated-helper IPC responses.</summary>
public static class InstallerMachineHelperResultCodec
{
    /// <summary>Maximum encoded helper response accepted over local IPC.</summary>
    public const int MaximumResultBytes = 4 * 1024;

    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "schema",
        "verb",
        "transactionId",
        "journalContentHash",
        "resultJournalContentHash",
        "resultJournalBase64",
        "outcome",
        "postconditionVerified",
        "diagnosticCode",
    };
    /// <summary>Serializes one validated result to compact UTF-8 without a BOM.</summary>
    public static byte[] Serialize(InstallerMachineHelperResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Validate();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", result.Schema);
            writer.WriteString("verb", VerbText(result.Verb));
            writer.WriteString("transactionId", result.TransactionId);
            writer.WriteString("journalContentHash", result.JournalContentHash);
            writer.WriteString("resultJournalContentHash", result.ResultJournalContentHash);
            writer.WriteString("resultJournalBase64", result.ResultJournalBase64);
            writer.WriteString("outcome", OutcomeText(result.Outcome));
            writer.WriteBoolean("postconditionVerified", result.PostconditionVerified);
            writer.WriteString("diagnosticCode", result.DiagnosticCode);
            if (result.DirectoryCleanupReport is { } cleanup)
            {
                writer.WriteStartArray("directoryCleanup");
                foreach (InstallerDirectoryCleanupEntry entry in cleanup.Entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", RoleText(entry.Role));
                    writer.WriteString("disposition", DispositionText(entry.Disposition));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        byte[] bytes = buffer.WrittenSpan.ToArray();
        ValidateSize(bytes);
        return bytes;
    }

    /// <summary>Parses an exact result without accepting unknown or duplicate fields.</summary>
    public static InstallerMachineHelperResult Parse(ReadOnlySpan<byte> bytes)
    {
        ValidateSize(bytes);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The helper result must be an object.");
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if ((!RequiredProperties.Contains(property.Name) && property.Name != "directoryCleanup")
                    || !observed.Add(property.Name))
                {
                    throw new JsonException(
                        "The helper result has an unknown or duplicate property.");
                }

                bool validType = property.Name switch
                {
                    "schema" => property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out _),
                    "verb" or "transactionId" or "journalContentHash"
                        or "resultJournalContentHash" or "resultJournalBase64" or "outcome"
                        or "diagnosticCode" => property.Value.ValueKind == JsonValueKind.String,
                    "postconditionVerified" =>
                        property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "directoryCleanup" => property.Value.ValueKind == JsonValueKind.Array,
                    _ => false,
                };
                if (!validType)
                {
                    throw new JsonException("A helper result property has an invalid type.");
                }
            }

            if (!RequiredProperties.IsSubsetOf(observed))
            {
                throw new JsonException("The helper result property set is incomplete.");
            }

            InstallerMachineHelperResult result = new(
                root.GetProperty("schema").GetInt32(),
                ParseVerb(root.GetProperty("verb").GetString()),
                root.GetProperty("transactionId").GetString()
                    ?? throw new JsonException("The helper result transaction is null."),
                root.GetProperty("journalContentHash").GetString()
                    ?? throw new JsonException("The helper result journal hash is null."),
                root.GetProperty("resultJournalContentHash").GetString()
                    ?? throw new JsonException("The helper result state hash is null."),
                root.GetProperty("resultJournalBase64").GetString()
                    ?? throw new JsonException("The helper result state payload is null."),
                ParseOutcome(root.GetProperty("outcome").GetString()),
                root.GetProperty("postconditionVerified").GetBoolean(),
                root.GetProperty("diagnosticCode").GetString()
                    ?? throw new JsonException("The helper result diagnostic is null."))
            {
                DirectoryCleanupReport = root.TryGetProperty("directoryCleanup", out JsonElement cleanup)
                    ? ParseCleanup(cleanup) : null,
            };
            result.Validate();
            byte[] canonical = Serialize(result);
            if (!CryptographicOperations.FixedTimeEquals(bytes, canonical))
            {
                throw new JsonException("The helper result is not canonical UTF-8 JSON.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_json_invalid",
                exception);
        }
    }

    private static void ValidateSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumResultBytes)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_size_invalid");
        }
    }

    private static InstallerDirectoryCleanupReport ParseCleanup(JsonElement array)
    {
        if (array.GetArrayLength() != InstallerDirectoryCleanupReport.DirectoryCount)
        {
            throw new JsonException("The directory cleanup observations are incomplete.");
        }
        var entries = new List<InstallerDirectoryCleanupEntry>(InstallerDirectoryCleanupReport.DirectoryCount);
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A directory cleanup observation must be an object.");
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (property.Name is not ("role" or "disposition")
                    || !names.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("A directory cleanup observation property is invalid.");
                }
            }
            if (names.Count != 2)
            {
                throw new JsonException("A directory cleanup observation is incomplete.");
            }
            entries.Add(new InstallerDirectoryCleanupEntry(
                ParseRole(item.GetProperty("role").GetString()),
                ParseDisposition(item.GetProperty("disposition").GetString())));
        }
        return new InstallerDirectoryCleanupReport(entries);
    }

    private static string RoleText(InstallerDirectoryRole role) => role switch
    {
        InstallerDirectoryRole.ProgramFilesProduct => "program-files-product",
        InstallerDirectoryRole.ProgramDataProduct => "program-data-product",
        InstallerDirectoryRole.InstallerRoot => "installer-root",
        InstallerDirectoryRole.InstallerVersion => "installer-version",
        InstallerDirectoryRole.AuthorityRoot => "authority-root",
        InstallerDirectoryRole.AuthorityVersion => "authority-version",
        _ => throw new InstallerProtocolException("installer.directory_cleanup.report_invalid"),
    };

    private static InstallerDirectoryRole ParseRole(string? value) => value switch
    {
        "program-files-product" => InstallerDirectoryRole.ProgramFilesProduct,
        "program-data-product" => InstallerDirectoryRole.ProgramDataProduct,
        "installer-root" => InstallerDirectoryRole.InstallerRoot,
        "installer-version" => InstallerDirectoryRole.InstallerVersion,
        "authority-root" => InstallerDirectoryRole.AuthorityRoot,
        "authority-version" => InstallerDirectoryRole.AuthorityVersion,
        _ => throw new JsonException("The directory role is invalid."),
    };

    private static string DispositionText(InstallerDirectoryCleanupDisposition disposition) => disposition switch
    {
        InstallerDirectoryCleanupDisposition.Missing => "missing",
        InstallerDirectoryCleanupDisposition.Deleted => "deleted",
        InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership => "retained-unproven-ownership",
        InstallerDirectoryCleanupDisposition.RetainedNonEmpty => "retained-non-empty",
        _ => throw new InstallerProtocolException("installer.directory_cleanup.report_invalid"),
    };

    private static InstallerDirectoryCleanupDisposition ParseDisposition(string? value) => value switch
    {
        "missing" => InstallerDirectoryCleanupDisposition.Missing,
        "deleted" => InstallerDirectoryCleanupDisposition.Deleted,
        "retained-unproven-ownership" => InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership,
        "retained-non-empty" => InstallerDirectoryCleanupDisposition.RetainedNonEmpty,
        _ => throw new JsonException("The directory disposition is invalid."),
    };

    private static string VerbText(InstallerMachineHelperVerb verb) => verb switch
    {
        InstallerMachineHelperVerb.Prepare => "prepare",
        InstallerMachineHelperVerb.CommitPackage => "commit-package",
        InstallerMachineHelperVerb.Apply => "apply",
        InstallerMachineHelperVerb.Remove => "remove",
        InstallerMachineHelperVerb.Verify => "verify",
        InstallerMachineHelperVerb.Clear => "clear",
        _ => throw new InstallerProtocolException("installer.machine_helper.verb_invalid"),
    };

    private static InstallerMachineHelperVerb ParseVerb(string? value) => value switch
    {
        "prepare" => InstallerMachineHelperVerb.Prepare,
        "commit-package" => InstallerMachineHelperVerb.CommitPackage,
        "apply" => InstallerMachineHelperVerb.Apply,
        "remove" => InstallerMachineHelperVerb.Remove,
        "verify" => InstallerMachineHelperVerb.Verify,
        "clear" => InstallerMachineHelperVerb.Clear,
        _ => throw new JsonException("The helper result verb is invalid."),
    };

    private static string OutcomeText(InstallerMachineHelperOutcome outcome) => outcome switch
    {
        InstallerMachineHelperOutcome.Succeeded => "succeeded",
        InstallerMachineHelperOutcome.Failed => "failed",
        InstallerMachineHelperOutcome.PostconditionFailed => "postcondition-failed",
        _ => throw new InstallerProtocolException(
            "installer.machine_helper.result_invalid"),
    };

    private static InstallerMachineHelperOutcome ParseOutcome(string? value) => value switch
    {
        "succeeded" => InstallerMachineHelperOutcome.Succeeded,
        "failed" => InstallerMachineHelperOutcome.Failed,
        "postcondition-failed" => InstallerMachineHelperOutcome.PostconditionFailed,
        _ => throw new JsonException("The helper result outcome is invalid."),
    };
}
