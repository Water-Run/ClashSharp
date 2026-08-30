using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Transactions;

/// <summary>Serializes and parses the canonical bounded installer journal document.</summary>
public static class InstallerTransactionCodec
{
    /// <summary>Gets the maximum accepted UTF-8 document length.</summary>
    public const int MaximumDocumentBytes = 1024;

    private static readonly string[] RequiredProperties =
    [
        "schema",
        "transactionId",
        "operation",
        "targetSid",
        "allowReassociation",
        "expectedPackageVersion",
        "installerPayloadSha256",
        "phase",
        "generation",
    ];

    /// <summary>Serializes one validated journal to canonical compact UTF-8 JSON.</summary>
    /// <param name="journal">Journal to serialize.</param>
    /// <returns>Canonical UTF-8 bytes.</returns>
    public static byte[] Serialize(InstallerTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", journal.Schema);
            writer.WriteString("transactionId", journal.TransactionId);
            writer.WriteString("operation", OperationText(journal.Operation));
            writer.WriteString("targetSid", journal.TargetSid);
            writer.WriteBoolean("allowReassociation", journal.AllowReassociation);
            writer.WriteString("expectedPackageVersion", journal.ExpectedPackageVersion);
            writer.WriteString("installerPayloadSha256", journal.InstallerPayloadSha256);
            writer.WriteString("phase", PhaseText(journal.Phase));
            writer.WriteNumber("generation", journal.Generation);
            writer.WriteEndObject();
        }

        byte[] bytes = buffer.WrittenSpan.ToArray();
        ValidateSize(bytes);
        return bytes;
    }

    /// <summary>Parses one strict, bounded, canonical UTF-8 JSON journal.</summary>
    /// <param name="bytes">UTF-8 input bytes.</param>
    /// <returns>A validated journal.</returns>
    public static InstallerTransactionJournal Parse(ReadOnlySpan<byte> bytes)
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
                throw new JsonException("The installer transaction must be an object.");
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            int propertyIndex = 0;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (propertyIndex >= RequiredProperties.Length
                    || !string.Equals(
                        property.Name,
                        RequiredProperties[propertyIndex],
                        StringComparison.Ordinal)
                    || !observed.Add(property.Name))
                {
                    throw new JsonException(
                        "The installer transaction has an unknown, duplicate, or reordered property.");
                }

                bool validType = property.Name switch
                {
                    "schema" or "generation" => property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out _),
                    "allowReassociation" =>
                        property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "transactionId" or "operation" or "targetSid" or "expectedPackageVersion"
                        or "installerPayloadSha256" or "phase" =>
                        property.Value.ValueKind == JsonValueKind.String,
                    _ => false,
                };
                if (!validType)
                {
                    throw new JsonException(
                        "An installer transaction property has an invalid value type.");
                }

                propertyIndex++;
            }

            if (propertyIndex != RequiredProperties.Length
                || !observed.SetEquals(RequiredProperties))
            {
                throw new JsonException("The installer transaction property set is incomplete.");
            }

            InstallerTransactionJournal journal = new(
                root.GetProperty("schema").GetInt32(),
                RequiredString(root, "transactionId"),
                ParseOperation(RequiredString(root, "operation")),
                RequiredString(root, "targetSid"),
                root.GetProperty("allowReassociation").GetBoolean(),
                RequiredString(root, "expectedPackageVersion"),
                RequiredString(root, "installerPayloadSha256"),
                ParsePhase(RequiredString(root, "phase")),
                root.GetProperty("generation").GetInt32());
            journal.Validate();

            byte[] canonical = Serialize(journal);
            if (!CryptographicOperations.FixedTimeEquals(bytes, canonical))
            {
                throw new JsonException("The installer transaction is not canonical UTF-8 JSON.");
            }

            return journal;
        }
        catch (JsonException exception)
        {
            throw new InstallerProtocolException("installer.transaction.json_invalid", exception);
        }
    }

    private static string RequiredString(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).GetString()
        ?? throw new JsonException("An installer transaction string is null.");

    private static InstallerOperation ParseOperation(string value) => value switch
    {
        "install" => InstallerOperation.Install,
        "repair" => InstallerOperation.Repair,
        "uninstall" => InstallerOperation.Uninstall,
        _ => throw new JsonException("The installer transaction operation is invalid."),
    };

    private static string OperationText(InstallerOperation operation) => operation switch
    {
        InstallerOperation.Install => "install",
        InstallerOperation.Repair => "repair",
        InstallerOperation.Uninstall => "uninstall",
        _ => throw new InstallerProtocolException("installer.request.operation_invalid"),
    };

    private static InstallerTransactionPhase ParsePhase(string value) => value switch
    {
        "prepared" => InstallerTransactionPhase.Prepared,
        "machineReserved" => InstallerTransactionPhase.MachineReserved,
        "machineRemovalAuthorized" => InstallerTransactionPhase.MachineRemovalAuthorized,
        "packageCommitted" => InstallerTransactionPhase.PackageCommitted,
        "machineCommitted" => InstallerTransactionPhase.MachineCommitted,
        "verified" => InstallerTransactionPhase.Verified,
        _ => throw new JsonException("The installer transaction phase is invalid."),
    };

    private static string PhaseText(InstallerTransactionPhase phase) => phase switch
    {
        InstallerTransactionPhase.Prepared => "prepared",
        InstallerTransactionPhase.MachineReserved => "machineReserved",
        InstallerTransactionPhase.MachineRemovalAuthorized => "machineRemovalAuthorized",
        InstallerTransactionPhase.PackageCommitted => "packageCommitted",
        InstallerTransactionPhase.MachineCommitted => "machineCommitted",
        InstallerTransactionPhase.Verified => "verified",
        _ => throw new InstallerProtocolException("installer.transaction.phase_invalid"),
    };

    private static void ValidateSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.transaction.size_invalid");
        }
    }
}
