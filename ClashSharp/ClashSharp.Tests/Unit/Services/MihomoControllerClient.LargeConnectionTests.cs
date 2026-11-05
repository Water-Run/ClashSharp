using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class MihomoControllerClientTests
{
    [Fact]
    public async Task AppConnectionReadersShareBoundedCountersAndOmittedDetailCapability()
    {
        string host = new('x', 240);
        string rows = string.Join(',', Enumerable.Range(0, 15000).Select(index =>
            $"{{\"id\":\"{index}\",\"metadata\":{{\"host\":\"{host}\"}},\"upload\":1,\"download\":2}}"));
        string json = "{\"connections\":[" + rows + "],\"uploadTotal\":1234,\"downloadTotal\":5678}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        Assert.True(bytes.Length > 4 * 1024 * 1024);
        using HttpClient http = new(new RecordingHttpHandler(json));
        MihomoControllerClient client = new(http, new Uri("http://127.0.0.1:9090"));
        IncompleteConnectionSnapshotException limited = await Assert.ThrowsAsync<IncompleteConnectionSnapshotException>(
            () => client.GetActiveConnectionsAsync(CancellationToken.None));
        Assert.Equal(15000, limited.TotalCount);
        Assert.Equal(15000, await client.GetActiveConnectionCountAsync(CancellationToken.None));
        MihomoTrafficSnapshot traffic = await client.GetTrafficSnapshotAsync(CancellationToken.None);
        Assert.False(traffic.ConnectionsComplete);
        Assert.Equal(15000, traffic.ActiveConnectionCount);
        Assert.Equal(1234, traffic.UploadTotalBytes);
        Assert.Equal(5678, traffic.DownloadTotalBytes);
        using ConnectionPayloadSocket socket = new(bytes);
        var frame = await MihomoControllerClient.ReceiveConnectionMessageAsync(socket, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(15000, frame.Value.OmittedCount);
        Assert.True(frame.Value.Content.Length < 256);
        using JsonDocument projected = JsonDocument.Parse(frame.Value.Content);
        Assert.Equal(1234, projected.RootElement.GetProperty("uploadTotal").GetInt64());
    }

    private sealed class ConnectionPayloadSocket(byte[] bytes) : WebSocket
    {
        private int _position;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(97, Math.Min(buffer.Count, bytes.Length - _position));
            bytes.AsSpan(_position, count).CopyTo(buffer.AsSpan());
            _position += count;
            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, _position == bytes.Length));
        }
    }
}
