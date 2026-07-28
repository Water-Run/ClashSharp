using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for tray status snapshot construction.</summary>
public sealed class TrayStatusServiceTests
{
    /// <summary>Verifies the primary runtime proxy group contributes current node and health latency.</summary>
    [Fact]
    public async Task GetSnapshotAsync_UsesPrimaryProxyGroupAndStoredLatency()
    {
        FakeRuntime runtime = new()
        {
            Groups =
            [
                new MihomoProxyGroup("Other", "Selector", "Node B", ["Node B"]),
                new MihomoProxyGroup("Proxy", "Selector", "Node A", ["Node A"]),
            ],
        };
        FakeHealthStorage healthStorage = new() { LatencyMilliseconds = 42 };
        TrayStatusService service = new(runtime, healthStorage, text => $"display:{text}");

        TrayStatusSnapshot snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("display:Node A", snapshot.CurrentNodeName);
        Assert.Equal("Node A", healthStorage.RequestedNodeName);
        Assert.Equal(42, snapshot.LatencyMilliseconds);
    }

    /// <summary>Verifies runtime failures produce an unavailable status snapshot.</summary>
    [Fact]
    public async Task GetSnapshotAsync_WhenRuntimeUnavailable_ReturnsUnavailable()
    {
        TrayStatusService service = new(new ThrowingRuntime(), new FakeHealthStorage(), text => text);

        TrayStatusSnapshot snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(TrayStatusSnapshot.Unavailable, snapshot);
    }

    /// <summary>Verifies caller cancellation is not converted into an unavailable snapshot.</summary>
    [Fact]
    public async Task GetSnapshotAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        TrayStatusService service = new(new CancelledRuntime(), new FakeHealthStorage(), text => text);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetSnapshotAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private sealed class FakeRuntime : ITrayStatusRuntime
    {
        public IReadOnlyList<MihomoProxyGroup> Groups { get; init; } = [];

        public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Groups);
        }
    }

    private sealed class ThrowingRuntime : ITrayStatusRuntime
    {
        public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<IReadOnlyList<MihomoProxyGroup>>(
                new InvalidOperationException("runtime unavailable"));
        }
    }

    private sealed class CancelledRuntime : ITrayStatusRuntime
    {
        public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
        {
            return Task.FromCanceled<IReadOnlyList<MihomoProxyGroup>>(cancellationToken);
        }
    }

    private sealed class FakeHealthStorage : ITrayStatusHealthStorage
    {
        public int? LatencyMilliseconds { get; init; }

        public string? RequestedNodeName { get; private set; }

        public int? GetNodeLatencyMilliseconds(string nodeName)
        {
            RequestedNodeName = nodeName;
            return LatencyMilliseconds;
        }
    }
}
