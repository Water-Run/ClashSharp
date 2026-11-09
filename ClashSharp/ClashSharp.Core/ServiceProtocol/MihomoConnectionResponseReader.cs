using System.Buffers;
using System.Text.Json;

namespace ClashSharp.ServiceProtocol;

/// <summary>Reads counters and row count with bounded memory, retaining small connection documents only.</summary>
public sealed class MihomoConnectionResponseReader
{
    private const int MaximumTokenBytes = 64 * 1024;
    private const int MaximumDetailBytes = 512 * 1024;
    private const long MaximumDocumentBytes = 64L * 1024 * 1024;
    private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = 32 });
    private readonly HashSet<string> _rootKeys = new(StringComparer.Ordinal);
    private string? _property;
    private long? _upload;
    private long? _download;
    private int _count;
    private int _nodes;
    private bool _connections;
    private bool _hasConnections;
    private bool _ended;

    private MihomoConnectionResponseReader() { }

    /// <summary>Retains a small complete document or projects only trustworthy counters and total row count.</summary>
    /// <param name="stream">Untrusted UTF-8 connection document.</param>
    /// <param name="token">Cancels scanning, including a stalled response body.</param>
    /// <returns>Bounded JSON plus an omitted row count when details exceed their budget.</returns>
    public static async Task<(byte[] Content, int? OmittedCount)> ReadAsync(Stream stream, CancellationToken token)
    {
        MihomoConnectionResponseReader parser = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaximumTokenBytes);
        using MemoryStream details = new();
        bool retain = true;
        int pending = 0;
        long total = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(pending, MaximumTokenBytes - pending), token).ConfigureAwait(false);
                total += read;
                if (total > MaximumDocumentBytes) { throw new InvalidDataException("The connection response exceeds the scan budget."); }
                if (retain && total <= MaximumDetailBytes) { details.Write(buffer, pending, read); }
                else { retain = false; }
                int available = pending + read;
                int consumed = parser.Consume(buffer.AsSpan(0, available), read == 0);
                pending = available - consumed;
                if (pending == MaximumTokenBytes) { throw new InvalidDataException("A connection JSON token exceeds the safety limit."); }
                buffer.AsSpan(consumed, pending).CopyTo(buffer);
                retain &= parser._nodes <= 80_000 && parser._count <= MihomoServiceIpcProtocol.MaximumControllerConnections;
                if (read == 0) { break; }
            }
            if (pending != 0 || !parser._ended || !parser._hasConnections || parser._upload is null || parser._download is null)
            {
                throw new InvalidDataException("The connection counters are incomplete.");
            }
            return retain ? (details.ToArray(), null) : (JsonSerializer.SerializeToUtf8Bytes(new
            {
                connections = (object?)null,
                uploadTotal = parser._upload.Value,
                downloadTotal = parser._download.Value,
            }), parser._count);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    // Reader state survives chunk boundaries; no connection strings or rows survive a discarded document.
    private int Consume(ReadOnlySpan<byte> bytes, bool final)
    {
        Utf8JsonReader reader = new(bytes, final, _state);
        while (reader.Read())
        {
            ++_nodes;
            if (reader.CurrentDepth == 0)
            {
                if (reader.TokenType == JsonTokenType.EndObject) { _ended = true; }
                else if (reader.TokenType != JsonTokenType.StartObject) { throw new InvalidDataException("Expected connection object."); }
            }
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                try { _property = reader.GetString()!; }
                catch (InvalidOperationException failure) { throw new InvalidDataException("Invalid connection root properties.", failure); }
                if (_rootKeys.Count >= 128 || !_rootKeys.Add(_property)) { throw new InvalidDataException("Invalid connection root properties."); }
            }
            else if (reader.CurrentDepth == 1)
            {
                if (_property == "connections")
                {
                    if (reader.TokenType == JsonTokenType.StartArray) { _connections = true; _hasConnections = true; }
                    else if (reader.TokenType == JsonTokenType.Null) { _hasConnections = true; }
                    else if (reader.TokenType == JsonTokenType.EndArray) { _connections = false; }
                    else { throw new InvalidDataException("Invalid connections array."); }
                }
                else if (_property is "uploadTotal" or "downloadTotal")
                {
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long value) || value < 0)
                    { throw new InvalidDataException("Invalid cumulative connection counter."); }
                    if (_property == "uploadTotal") { _upload = value; } else { _download = value; }
                }
            }
            else if (_connections && reader.CurrentDepth == 2 && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.StartObject) { throw new InvalidDataException("Invalid connection row."); }
                _count = checked(_count + 1);
            }
        }
        _state = reader.CurrentState;
        return checked((int)reader.BytesConsumed);
    }
}
