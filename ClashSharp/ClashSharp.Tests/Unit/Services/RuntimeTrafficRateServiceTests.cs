/*
 * Runtime Traffic Rate Service Tests
 * Verifies realtime traffic rate snapshots derived from active connection counters
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/RuntimeTrafficRateServiceTests.cs
 * @date: 2026-06-29
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for realtime traffic rate snapshots.</summary>
public sealed class RuntimeTrafficRateServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_FirstSample_ReturnsZeroRatesAndCurrentConnectionCount()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-29T08:00:00Z");
        FakeRuntimeTrafficConnections connections = new()
        {
            Connections =
            [
                CreateConnection("a", uploadBytes: 100, downloadBytes: 200),
                CreateConnection("b", uploadBytes: 50, downloadBytes: 75),
            ],
        };
        RuntimeTrafficRateService service = new(connections, () => now);

        RuntimeTrafficRateSnapshot snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(0, snapshot.UploadBytesPerSecond);
        Assert.Equal(0, snapshot.DownloadBytesPerSecond);
        Assert.Equal(2, snapshot.ActiveConnectionCount);
        Assert.Equal(0, snapshot.SessionUploadBytes);
        Assert.Equal(0, snapshot.SessionDownloadBytes);
    }

    [Fact]
    public async Task GetSnapshotAsync_SecondSample_ReturnsRatesAndSessionTrafficFromCounterDeltas()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-29T08:00:00Z");
        FakeRuntimeTrafficConnections connections = new()
        {
            Connections = [CreateConnection("a", uploadBytes: 100, downloadBytes: 200)],
        };
        RuntimeTrafficRateService service = new(connections, () => now);
        await service.GetSnapshotAsync(CancellationToken.None);

        now = now.AddSeconds(2);
        connections.Connections = [CreateConnection("a", uploadBytes: 160, downloadBytes: 320)];

        RuntimeTrafficRateSnapshot snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(30, snapshot.UploadBytesPerSecond);
        Assert.Equal(60, snapshot.DownloadBytesPerSecond);
        Assert.Equal(1, snapshot.ActiveConnectionCount);
        Assert.Equal(60, snapshot.SessionUploadBytes);
        Assert.Equal(120, snapshot.SessionDownloadBytes);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenConnectionCounterResets_UsesCurrentCounterAsDelta()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-29T08:00:00Z");
        FakeRuntimeTrafficConnections connections = new()
        {
            Connections = [CreateConnection("a", uploadBytes: 100, downloadBytes: 200)],
        };
        RuntimeTrafficRateService service = new(connections, () => now);
        await service.GetSnapshotAsync(CancellationToken.None);

        now = now.AddSeconds(5);
        connections.Connections = [CreateConnection("a", uploadBytes: 20, downloadBytes: 40)];

        RuntimeTrafficRateSnapshot snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(4, snapshot.UploadBytesPerSecond);
        Assert.Equal(8, snapshot.DownloadBytesPerSecond);
        Assert.Equal(20, snapshot.SessionUploadBytes);
        Assert.Equal(40, snapshot.SessionDownloadBytes);
    }

    private static ActiveConnection CreateConnection(string id, long uploadBytes, long downloadBytes)
    {
        return new ActiveConnection(
            id,
            string.Empty,
            "example.com",
            "MATCH",
            string.Empty,
            "Proxy",
            uploadBytes,
            downloadBytes,
            DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeRuntimeTrafficConnections : IRuntimeTrafficConnections
    {
        public IReadOnlyList<ActiveConnection> Connections { get; set; } = [];

        public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Connections);
        }
    }
}
