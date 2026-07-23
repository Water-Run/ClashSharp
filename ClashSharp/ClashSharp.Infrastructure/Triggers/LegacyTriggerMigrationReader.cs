using System.Security.Cryptography;
using System.Text.Json;

namespace ClashSharp.Infrastructure.Triggers;

internal static class LegacyTriggerMigrationReader
{
    private const int MaximumDocumentBytes = 16 * 1024 * 1024;

    public static async Task<LegacyTriggerDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes);
            JsonElement root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return new LegacyTriggerDocument(
                    sourceHash,
                    root.EnumerateArray(),
                    documentErrorCode: null);
            }

            if (root.ValueKind != JsonValueKind.Object
                || !TryGetProperty(root, "Tasks", out JsonElement tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                return new LegacyTriggerDocument(
                    sourceHash,
                    [],
                    "trigger.migration.document.shape_invalid");
            }

            return new LegacyTriggerDocument(
                sourceHash,
                tasks.EnumerateArray(),
                documentErrorCode: null);
        }
        catch (JsonException)
        {
            return new LegacyTriggerDocument(
                sourceHash,
                [],
                "trigger.migration.document.json_malformed");
        }
    }

    public static async Task<string> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static async Task<byte[]> ReadBytesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("Legacy trigger document exceeds the migration size limit.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }
}
