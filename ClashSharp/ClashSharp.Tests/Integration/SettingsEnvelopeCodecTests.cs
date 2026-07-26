using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies canonical settings-envelope encoding and strict external-input parsing.</summary>
public sealed class SettingsEnvelopeCodecTests
{
    /// <summary>Verifies identical domain state always produces identical canonical bytes.</summary>
    [Fact]
    public void Encode_EquivalentEnvelope_IsByteDeterministic()
    {
        SettingsEnvelope envelope = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);

        SettingsEnvelopeCodec.EncodedSettingsEnvelope first =
            SettingsEnvelopeCodec.Encode(envelope, SettingsRegistry.Default);
        SettingsEnvelopeCodec.EncodedSettingsEnvelope second =
            SettingsEnvelopeCodec.Encode(envelope, SettingsRegistry.Default);
        SettingsEnvelope decoded =
            SettingsEnvelopeCodec.Decode(first.Bytes, SettingsRegistry.Default);

        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(
            first.Bytes,
            SettingsEnvelopeCodec.Encode(decoded, SettingsRegistry.Default).Bytes);
    }

    /// <summary>Verifies first-use races cannot escape as static-construction failures.</summary>
    [Fact]
    public async Task Encode_ConcurrentFirstUse_DoesNotThrowStaticConstructionFailure()
    {
        SettingsEnvelope envelope = SettingsEnvelopeTestData.CreateMatchingEnvelope();

        byte[][] encoded = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() =>
                    SettingsEnvelopeCodec
                        .Encode(envelope, SettingsRegistry.Default)
                        .Bytes)));

        Assert.All(encoded, bytes => Assert.Equal(encoded[0], bytes));
    }

    /// <summary>Verifies hash mismatch is rejected before a payload can be trusted.</summary>
    [Fact]
    public void Decode_HashMismatch_IsRejected()
    {
        SettingsEnvelopeCodec.EncodedSettingsEnvelope encoded =
            SettingsEnvelopeCodec.Encode(
                SettingsEnvelopeTestData.CreateMatchingEnvelope(),
                SettingsRegistry.Default);
        byte[] tampered = RewriteContentHash(encoded.Bytes);

        SettingsEnvelopeCodecException exception =
            Assert.Throws<SettingsEnvelopeCodecException>(
                () => SettingsEnvelopeCodec.Decode(
                    tampered,
                    SettingsRegistry.Default));

        Assert.Equal("settings.persistence.envelope.hash_mismatch", exception.Code);
    }

    /// <summary>Verifies JSON numeric enum forms are rejected rather than normalized.</summary>
    [Fact]
    public void Decode_NumericEnum_IsRejected()
    {
        SettingsEnvelopeCodec.EncodedSettingsEnvelope encoded =
            SettingsEnvelopeCodec.Encode(
                SettingsEnvelopeTestData.CreatePendingEnvelope(
                    [("AppThemeMode", "Dark")]),
                SettingsRegistry.Default);
        byte[] tampered = RewritePayload(
            encoded.Bytes,
            payload => payload.Replace(
                "\"kind\":\"liveReconcile\"",
                "\"kind\":0",
                StringComparison.Ordinal));

        SettingsEnvelopeCodecException exception =
            Assert.Throws<SettingsEnvelopeCodecException>(
                () => SettingsEnvelopeCodec.Decode(
                    tampered,
                    SettingsRegistry.Default));

        Assert.Equal("settings.persistence.envelope.enum_invalid", exception.Code);
    }

    /// <summary>Verifies equivalent non-integer JSON number spellings are rejected.</summary>
    [Fact]
    public void Decode_NonCanonicalInteger_IsRejected()
    {
        SettingsEnvelopeCodec.EncodedSettingsEnvelope encoded =
            SettingsEnvelopeCodec.Encode(
                SettingsEnvelopeTestData.CreateMatchingEnvelope(),
                SettingsRegistry.Default);
        byte[] tampered = RewritePayload(
            encoded.Bytes,
            payload => payload.Replace(
                "\"envelopeRevision\":1",
                "\"envelopeRevision\":1.0",
                StringComparison.Ordinal));

        SettingsEnvelopeCodecException exception =
            Assert.Throws<SettingsEnvelopeCodecException>(
                () => SettingsEnvelopeCodec.Decode(
                    tampered,
                    SettingsRegistry.Default));

        Assert.Equal("settings.persistence.envelope.number_noncanonical", exception.Code);
    }

    /// <summary>Verifies unknown and duplicate members are rejected at the storage boundary.</summary>
    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"unexpected\":true")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    public void Decode_UnexpectedShape_IsRejected(string source, string replacement)
    {
        SettingsEnvelopeCodec.EncodedSettingsEnvelope encoded =
            SettingsEnvelopeCodec.Encode(
                SettingsEnvelopeTestData.CreateMatchingEnvelope(),
                SettingsRegistry.Default);
        byte[] tampered = RewritePayload(
            encoded.Bytes,
            payload => payload.Replace(source, replacement, StringComparison.Ordinal));

        SettingsEnvelopeCodecException exception =
            Assert.Throws<SettingsEnvelopeCodecException>(
                () => SettingsEnvelopeCodec.Decode(
                    tampered,
                    SettingsRegistry.Default));

        Assert.Equal("settings.persistence.envelope.shape_invalid", exception.Code);
    }

    private static byte[] RewritePayload(
        byte[] envelopeBytes,
        Func<string, string> rewrite)
    {
        using JsonDocument envelope = JsonDocument.Parse(envelopeBytes);
        byte[] payloadBytes = Convert.FromBase64String(
            envelope.RootElement.GetProperty("payload").GetString()!);
        string rewrittenPayload = rewrite(Encoding.UTF8.GetString(payloadBytes));
        byte[] rewrittenBytes = Encoding.UTF8.GetBytes(rewrittenPayload);
        string hash = Convert.ToHexStringLower(SHA256.HashData(rewrittenBytes));
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteBase64String("payload", rewrittenBytes);
            writer.WriteString("contentHash", hash);
            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    private static byte[] RewriteContentHash(byte[] envelopeBytes)
    {
        using JsonDocument envelope = JsonDocument.Parse(envelopeBytes);
        string contentHash =
            envelope.RootElement.GetProperty("contentHash").GetString()!;
        char replacement = contentHash[0] == '0' ? '1' : '0';
        string rewrittenHash = replacement + contentHash[1..];
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteString(
                "payload",
                envelope.RootElement.GetProperty("payload").GetString());
            writer.WriteString("contentHash", rewrittenHash);
            writer.WriteEndObject();
        }

        return output.ToArray();
    }
}
