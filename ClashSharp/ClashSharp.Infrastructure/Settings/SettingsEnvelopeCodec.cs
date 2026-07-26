using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Settings;

namespace ClashSharp.Infrastructure.Settings;

internal static partial class SettingsEnvelopeCodec
{
    private const int FormatVersion = 1;

    public static EncodedSettingsEnvelope Encode(
        SettingsEnvelope envelope,
        SettingsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(registry);
        SettingsEnvelopeValidationResult validation =
            new SettingsEnvelopeValidator(registry).Validate(envelope);
        if (!validation.IsValid)
        {
            SettingsEnvelopeValidationError first = validation.Errors[0];
            throw new SettingsEnvelopeCodecException(
                "settings.persistence.envelope.domain_invalid",
                first.Path);
        }

        byte[] payload = WritePayload(envelope);
        string contentHash =
            Convert.ToHexStringLower(SHA256.HashData(payload));
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteBase64String("payload", payload);
            writer.WriteString("contentHash", contentHash);
            writer.WriteEndObject();
        }

        return new EncodedSettingsEnvelope(output.ToArray(), contentHash);
    }

    public static SettingsEnvelope Decode(
        ReadOnlyMemory<byte> bytes,
        SettingsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            IReadOnlyDictionary<string, JsonElement> properties = ReadShape(
                document.RootElement,
                ["formatVersion", "payload", "contentHash"],
                "$");
            int formatVersion = ReadCanonicalInt32(
                properties["formatVersion"],
                "$.formatVersion");
            if (formatVersion != FormatVersion)
            {
                throw Error(
                    "settings.persistence.envelope.format_unsupported",
                    "$.formatVersion");
            }

            string payloadText = ReadString(properties["payload"], "$.payload");
            byte[] payload = DecodeCanonicalBase64(payloadText, "$.payload");
            string contentHash = ReadCanonicalHash(
                properties["contentHash"],
                "$.contentHash");
            string actualHash =
                Convert.ToHexStringLower(SHA256.HashData(payload));
            if (!string.Equals(contentHash, actualHash, StringComparison.Ordinal))
            {
                throw Error(
                    "settings.persistence.envelope.hash_mismatch",
                    "$.contentHash");
            }

            SettingsEnvelope envelope = ReadPayload(payload, registry);
            EncodedSettingsEnvelope canonical = Encode(envelope, registry);
            if (!bytes.Span.SequenceEqual(canonical.Bytes))
            {
                throw Error(
                    "settings.persistence.envelope.noncanonical",
                    "$");
            }

            return envelope;
        }
        catch (SettingsEnvelopeCodecException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Error(
                "settings.persistence.envelope.json_invalid",
                "$",
                exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FormatException
                or OverflowException)
        {
            throw Error(
                "settings.persistence.envelope.domain_invalid",
                "$",
                exception);
        }
    }

    internal sealed record EncodedSettingsEnvelope(
        byte[] Bytes,
        string ContentHash);

    private static SettingsEnvelopeCodecException Error(
        string code,
        string path,
        Exception? innerException = null) =>
        new(code, path, innerException);
}
