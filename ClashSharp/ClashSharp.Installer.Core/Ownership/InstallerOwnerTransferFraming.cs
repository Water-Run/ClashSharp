using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;

namespace ClashSharp.Installer.Ownership;

/// <summary>
/// Strict, bounded public handshake frames on the authenticated helper pipe. The successful handoff
/// uses an ordinary Prepared command frame; private transfer journals are never serializable here.
/// </summary>
public static class InstallerOwnerTransferFraming
{
    private const int MaximumFrameBytes = 4096;

    /// <summary>Writes the parent's public candidate request.</summary>
    public static Task WriteRequestAsync(Stream stream, InstallerOwnerTransferRequest request, CancellationToken cancellationToken) =>
        InstallerMachineHelperFraming.WriteFrameAsync(stream, Serialize(request), cancellationToken);

    /// <summary>Reads exactly one canonical request.</summary>
    public static async Task<InstallerOwnerTransferRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken) =>
        ParseRequest(await ReadAsync(stream, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes the helper's public confirmation offer.</summary>
    public static Task WriteOfferAsync(Stream stream, InstallerOwnerTransferOffer offer, CancellationToken cancellationToken) =>
        InstallerMachineHelperFraming.WriteFrameAsync(stream, Serialize(offer), cancellationToken);

    /// <summary>Reads exactly one canonical offer.</summary>
    public static async Task<InstallerOwnerTransferOffer> ReadOfferAsync(Stream stream, CancellationToken cancellationToken) =>
        ParseOffer(await ReadAsync(stream, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes the user's session-bound decision.</summary>
    public static Task WriteDecisionAsync(Stream stream, InstallerOwnerTransferDecision decision, CancellationToken cancellationToken) =>
        InstallerMachineHelperFraming.WriteFrameAsync(stream, Serialize(decision), cancellationToken);

    /// <summary>Reads exactly one canonical decision.</summary>
    public static async Task<InstallerOwnerTransferDecision> ReadDecisionAsync(Stream stream, CancellationToken cancellationToken) =>
        ParseDecision(await ReadAsync(stream, cancellationToken).ConfigureAwait(false));

    private static Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        InstallerMachineHelperFraming.ReadFrameAsync(stream, MaximumFrameBytes, cancellationToken);

    private static byte[] Serialize(InstallerOwnerTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return Write("request", writer => WriteRequest(writer, request));
    }

    private static byte[] Serialize(InstallerOwnerTransferOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        offer.Validate();
        return Write("offer", writer =>
        {
            WriteRequest(writer, offer.Request);
            writer.WriteString("offerId", offer.OfferId);
            writer.WriteString("previousOwnerSid", offer.PreviousOwnerSid);
            writer.WriteString("nextOwnerSid", offer.NextOwnerSid);
            writer.WriteBoolean("isRecovery", offer.IsRecovery);
        });
    }

    private static byte[] Serialize(InstallerOwnerTransferDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        InstallerProtocolValidation.ValidateLowerHex256(decision.SessionId, "installer.owner_transfer.session_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(decision.OfferId, "installer.owner_transfer.offer_invalid");
        return Write("decision", writer =>
        {
            writer.WriteString("sessionId", decision.SessionId);
            writer.WriteString("offerId", decision.OfferId);
            writer.WriteBoolean("accepted", decision.Accepted);
        });
    }

    private static byte[] Write(string kind, Action<Utf8JsonWriter> content)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("kind", kind);
            content(writer);
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > MaximumFrameBytes)
        {
            throw Invalid();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteRequest(Utf8JsonWriter writer, InstallerOwnerTransferRequest request)
    {
        writer.WriteString("sessionId", request.SessionId);
        writer.WriteString("operation", request.Operation == InstallerOperation.Install ? "install" : "repair");
        writer.WriteString("expectedPackageVersion", request.ExpectedPackageVersion);
        writer.WriteString("installerPayloadSha256", request.InstallerPayloadSha256);
    }

    private static InstallerOwnerTransferRequest ParseRequest(byte[] bytes) =>
        Parse(bytes, "request", ReadRequest, Serialize);

    private static InstallerOwnerTransferOffer ParseOffer(byte[] bytes) =>
        Parse(bytes, "offer", root => new InstallerOwnerTransferOffer(ReadRequest(root),
            Text(root, "offerId"), Text(root, "previousOwnerSid"), Text(root, "nextOwnerSid"),
            root.GetProperty("isRecovery").GetBoolean()), Serialize);

    private static InstallerOwnerTransferDecision ParseDecision(byte[] bytes) =>
        Parse(bytes, "decision", root => new InstallerOwnerTransferDecision(Text(root, "sessionId"),
            Text(root, "offerId"), root.GetProperty("accepted").GetBoolean()), Serialize);

    private static InstallerOwnerTransferRequest ReadRequest(JsonElement root) => new(Text(root, "sessionId"),
        Text(root, "operation") switch
        {
            "install" => InstallerOperation.Install,
            "repair" => InstallerOperation.Repair,
            _ => throw Invalid(),
        }, Text(root, "expectedPackageVersion"), Text(root, "installerPayloadSha256"));

    private static string Text(JsonElement root, string name) => root.GetProperty(name).GetString() ?? throw Invalid();

    private static T Parse<T>(byte[] bytes, string kind, Func<JsonElement, T> read, Func<T, byte[]> serialize)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
            JsonElement root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1 || Text(root, "kind") != kind)
            {
                throw Invalid();
            }
            T value = read(root);
            // Canonical equality also rejects unknown/duplicate fields, alternate field order,
            // whitespace, escaped aliases, number spellings and any trailing content.
            if (!CryptographicOperations.FixedTimeEquals(bytes, serialize(value)))
            {
                throw Invalid();
            }
            return value;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InstallerProtocolException("installer.owner_transfer.frame_invalid", exception);
        }
    }

    private static InstallerProtocolException Invalid() => new("installer.owner_transfer.frame_invalid");
}
