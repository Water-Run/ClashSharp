using System.Globalization;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for realtime traffic rate snapshots.</summary>
public sealed class RuntimeTrafficRateServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_FirstSample_ReturnsZeroRatesAndCurrentConnectionCount()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-29T08:00:00Z", CultureInfo.InvariantCulture);
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
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-29T08:00:00Z", CultureInfo.InvariantCulture);
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
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-29T08:00:00Z", CultureInfo.InvariantCulture);
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

    [Fact]
    public async Task GetSnapshotAsync_ConcurrentConsumersShareSampleWithoutZeroingRate()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TaskCompletionSource<IReadOnlyList<ActiveConnection>> response = new();
        FakeRuntimeTrafficConnections connections = new() { Handler = _ => response.Task };
        RuntimeTrafficRateService service = new(connections, () => now);
        Task<RuntimeTrafficRateSnapshot> dashboard = service.GetSnapshotAsync(CancellationToken.None);
        Task<RuntimeTrafficRateSnapshot> trigger = service.GetSnapshotAsync(CancellationToken.None);
        response.SetResult([CreateConnection("a", 0, 0)]);
        Assert.Equal(await dashboard, await trigger);
        Assert.Equal(1, connections.ReadCount);

        now += TimeSpan.FromSeconds(1);
        connections.Handler = null;
        connections.Connections = [CreateConnection("a", 0, 4096)];
        RuntimeTrafficRateSnapshot first = await service.GetSnapshotAsync(CancellationToken.None);
        RuntimeTrafficRateSnapshot second = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(4096, first.DownloadBytesPerSecond);
        Assert.Equal(first, second);
        Assert.Equal(2, connections.ReadCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_CancelledQueuedConsumerDoesNotCancelActiveSample()
    {
        using CancellationTokenSource queuedLifetime = new();
        TaskCompletionSource<IReadOnlyList<ActiveConnection>> response = new();
        FakeRuntimeTrafficConnections connections = new() { Handler = _ => response.Task };
        RuntimeTrafficRateService service = new(connections);
        Task<RuntimeTrafficRateSnapshot> first = service.GetSnapshotAsync(CancellationToken.None);
        Task<RuntimeTrafficRateSnapshot> queued = service.GetSnapshotAsync(queuedLifetime.Token);
        queuedLifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        response.SetResult([CreateConnection("a", 10, 10)]);
        Assert.Equal(1, (await first).ActiveConnectionCount);
        Assert.Equal(1, connections.ReadCount);
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

        public Guid Epoch { get; set; } = Guid.NewGuid();

        public long? UploadTotal { get; set; }

        public long? DownloadTotal { get; set; }

        public Func<CancellationToken, Task<IReadOnlyList<ActiveConnection>>>? Handler { get; set; }

        public int ReadCount { get; private set; }

        public async Task<MihomoTrafficSnapshot> GetTrafficSnapshotAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ++ReadCount;
            IReadOnlyList<ActiveConnection> rows = Handler is not null
                ? await Handler(cancellationToken)
                : Connections;
            return new MihomoTrafficSnapshot(Epoch,
                UploadTotal ?? rows.Sum(row => row.UploadBytes),
                DownloadTotal ?? rows.Sum(row => row.DownloadBytes), rows);
        }
    }

    [Fact]
    public async Task ClosedAndBetweenSampleConnections_ContributeTheirFullCounterDelta()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FakeRuntimeTrafficConnections connections = new()
        {
            UploadTotal = 100,
            DownloadTotal = 1000,
            Connections = [CreateConnection("still-open", 100, 1000)],
        };
        RuntimeTrafficRateService service = new(connections, () => now);
        await service.GetSnapshotAsync(CancellationToken.None);

        // The original connection closes and a short request begins and ends between reads.
        // Neither appears in the second active list; the core totals retain both tails.
        connections.Connections = [];
        connections.UploadTotal = 350;
        connections.DownloadTotal = 201000;
        now = now.AddSeconds(2);
        RuntimeTrafficRateSnapshot sample = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(0, sample.ActiveConnectionCount);
        Assert.Equal(250, sample.SessionUploadBytes);
        Assert.Equal(200000, sample.SessionDownloadBytes);
        Assert.Equal(100000, sample.DownloadBytesPerSecond);
        now = now.AddSeconds(1);
        RuntimeTrafficRateSnapshot repeated = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(200000, repeated.SessionDownloadBytes);
        Assert.Equal(0, repeated.DownloadBytesPerSecond);
    }

    [Fact]
    public async Task RestartWithHigherCounters_DoesNotSubtractThePreviousCoreBaseline()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FakeRuntimeTrafficConnections connections = new() { UploadTotal = 100, DownloadTotal = 200 };
        RuntimeTrafficRateService service = new(connections, () => now);
        await service.GetSnapshotAsync(CancellationToken.None);

        connections.Epoch = Guid.NewGuid();
        connections.UploadTotal = 300;
        connections.DownloadTotal = 1000;
        now = now.AddSeconds(1);
        RuntimeTrafficRateSnapshot sample = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(300, sample.SessionUploadBytes);
        Assert.Equal(1000, sample.SessionDownloadBytes);
    }

    [Fact]
    public async Task FailedRead_DoesNotAdvanceTheCounterOrTimeBaseline()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FakeRuntimeTrafficConnections connections = new() { UploadTotal = 0, DownloadTotal = 0 };
        RuntimeTrafficRateService service = new(connections, () => now);
        await service.GetSnapshotAsync(CancellationToken.None);
        now = now.AddSeconds(1);
        connections.Handler = _ => throw new IOException("unavailable");
        await Assert.ThrowsAsync<IOException>(() => service.GetSnapshotAsync(CancellationToken.None));

        now = now.AddSeconds(1);
        connections.Handler = null;
        connections.DownloadTotal = 4096;
        RuntimeTrafficRateSnapshot sample = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(2048, sample.DownloadBytesPerSecond);
        Assert.Equal(4096, sample.SessionDownloadBytes);
    }

    [Fact]
    public async Task MaximumCounters_DoNotWrapRatesOrSessionTotalsNegative()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FakeRuntimeTrafficConnections connections = new() { UploadTotal = 0, DownloadTotal = 0 };
        RuntimeTrafficRateService service = new(connections, () => now);
        await service.GetSnapshotAsync(CancellationToken.None);
        now = now.AddSeconds(1);
        connections.DownloadTotal = long.MaxValue;
        RuntimeTrafficRateSnapshot sample = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(long.MaxValue, sample.DownloadBytesPerSecond);

        now = now.AddSeconds(1);
        connections.Epoch = Guid.NewGuid();
        sample = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(long.MaxValue, sample.SessionDownloadBytes);
    }
}
