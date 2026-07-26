using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Infrastructure.Data;

internal static class DataGenerationManifestCodec
{
    private const int EnvelopeSchemaVersion = 1;
    private const int PayloadSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static EncodedManifest Encode(
        DataGenerationPathPolicy pathPolicy,
        DataGenerationDescriptor descriptor,
        long manifestRevision,
        long highestGenerationNumber)
    {
        ManifestDocument document = new(
            PayloadSchemaVersion,
            manifestRevision,
            descriptor.GenerationId,
            descriptor.GenerationNumber,
            highestGenerationNumber,
            pathPolicy.GetRelativeGenerationPath(descriptor.GenerationId));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        string contentHash = ComputeHash(payload);
        ManifestEnvelope envelope = new(
            EnvelopeSchemaVersion,
            Convert.ToBase64String(payload),
            contentHash);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        return new EncodedManifest(bytes, contentHash);
    }

    public static DataGenerationManifestSnapshot Decode(
        ReadOnlySpan<byte> bytes,
        DataGenerationPathPolicy pathPolicy)
    {
        try
        {
            ManifestEnvelope envelope = JsonSerializer.Deserialize<ManifestEnvelope>(
                bytes,
                SerializerOptions)
                ?? throw CreateCorruptException("The current-generation envelope is empty.");
            if (envelope.SchemaVersion != EnvelopeSchemaVersion)
            {
                throw new DataGenerationStoreException(
                    DataGenerationStoreError.UnsupportedSchema,
                    $"Generation envelope schema '{envelope.SchemaVersion}' is unsupported.");
            }

            if (!DataGenerationManifestSnapshot.IsCanonicalContentHash(envelope.ContentHash)
                || string.IsNullOrWhiteSpace(envelope.Payload))
            {
                throw CreateCorruptException(
                    "The current-generation envelope contains invalid required fields.");
            }

            byte[] payload = Convert.FromBase64String(envelope.Payload);
            string actualHash = ComputeHash(payload);
            if (!string.Equals(actualHash, envelope.ContentHash, StringComparison.Ordinal))
            {
                throw CreateCorruptException(
                    "The current-generation payload hash does not match its envelope.");
            }

            ManifestDocument document = JsonSerializer.Deserialize<ManifestDocument>(
                payload,
                SerializerOptions)
                ?? throw CreateCorruptException("The current-generation payload is empty.");
            byte[] canonicalPayload =
                JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
            if (!payload.AsSpan().SequenceEqual(canonicalPayload))
            {
                throw CreateCorruptException(
                    "The current-generation payload is not canonical JSON.");
            }

            if (document.SchemaVersion != PayloadSchemaVersion)
            {
                throw new DataGenerationStoreException(
                    DataGenerationStoreError.UnsupportedSchema,
                    $"Generation manifest schema '{document.SchemaVersion}' is unsupported.");
            }

            ValidateDocument(document, pathPolicy);
            DataGenerationDescriptor descriptor = new(
                document.GenerationId,
                document.GenerationNumber,
                pathPolicy.GetGenerationRootPath(document.GenerationId));
            return new DataGenerationManifestSnapshot(
                descriptor,
                document.ManifestRevision,
                document.HighestGenerationNumber,
                actualHash);
        }
        catch (DataGenerationStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw CreateCorruptException(
                "The current-generation manifest cannot be parsed.",
                exception);
        }
    }

    public static void ValidateSnapshot(
        DataGenerationManifestSnapshot snapshot,
        DataGenerationPathPolicy pathPolicy)
    {
        EncodedManifest encoded = Encode(
            pathPolicy,
            snapshot.Descriptor,
            snapshot.ManifestRevision,
            snapshot.HighestGenerationNumber);
        if (!string.Equals(encoded.ContentHash, snapshot.ContentHash, StringComparison.Ordinal))
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.InvalidHash,
                "The supplied baseline snapshot hash does not match its canonical payload.");
        }
    }

    private static void ValidateDocument(
        ManifestDocument document,
        DataGenerationPathPolicy pathPolicy)
    {
        string expectedRelativePath = pathPolicy.GetRelativeGenerationPath(document.GenerationId);
        if (document.GenerationId == Guid.Empty
            || document.GenerationNumber < 1
            || document.ManifestRevision < 1
            || document.HighestGenerationNumber < document.GenerationNumber
            || !string.Equals(
                document.RootRelativePath,
                expectedRelativePath,
                StringComparison.Ordinal))
        {
            throw CreateCorruptException(
                "The current-generation payload contains invalid descriptor fields.");
        }
    }

    private static string ComputeHash(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DataGenerationStoreException CreateCorruptException(
        string message,
        Exception? innerException = null)
    {
        return new DataGenerationStoreException(
            DataGenerationStoreError.Corrupt,
            message,
            innerException);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowDuplicateProperties = false,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
    }

    internal sealed record EncodedManifest(byte[] Bytes, string ContentHash);

    private sealed record ManifestEnvelope(
        int SchemaVersion,
        string Payload,
        string ContentHash);

    private sealed record ManifestDocument(
        int SchemaVersion,
        long ManifestRevision,
        Guid GenerationId,
        long GenerationNumber,
        long HighestGenerationNumber,
        string RootRelativePath);
}
