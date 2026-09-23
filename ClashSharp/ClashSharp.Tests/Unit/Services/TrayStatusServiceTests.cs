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

    /// <summary>Verifies actual routing mode determines the displayed node, including direct standby.</summary>
    [Theory]
    [InlineData(ClashSharpMode.FullTakeover, "Global node", 42)]
    [InlineData(ClashSharpMode.RuleTakeover, "Rule node", 42)]
    [InlineData(ClashSharpMode.Standby, "DIRECT", null)]
    [InlineData(ClashSharpMode.Disabled, "", null)]
    [InlineData(ClashSharpMode.Faulted, "", null)]
    public async Task GetSnapshotAsync_UsesRoutingMode(ClashSharpMode mode, string expectedNode, int? expectedLatency)
    {
        FakeRuntime runtime = new()
        {
            Mode = mode,
            Groups =
            [
                new("GLOBAL", "Selector", "Global node", ["Global node"]),
                new("Proxy", "Selector", "Rule node", ["Rule node"]),
            ],
        };
        FakeHealthStorage health = new() { LatencyMilliseconds = 42 };
        TrayStatusService service = new(runtime, health, text => text);

        TrayStatusSnapshot snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(expectedNode, snapshot.CurrentNodeName);
        Assert.Equal(expectedLatency, snapshot.LatencyMilliseconds);
        Assert.Equal(expectedLatency is null ? null : expectedNode, health.RequestedNodeName);
    }

    /// <summary>Verifies nested groups resolve to leaf health and cycles never expose a false node.</summary>
    [Theory]
    [InlineData("Actual node", "Actual node")]
    [InlineData("GLOBAL", "")]
    [InlineData("Nested", "")]
    [InlineData("", "")]
    public async Task GetSnapshotAsync_ResolvesNestedSelection(string nestedSelection, string expectedNode)
    {
        FakeRuntime runtime = new()
        {
            Mode = ClashSharpMode.FullTakeover,
            Groups =
            [
                new("GLOBAL", "Selector", "Nested", ["Nested"]),
                new("Nested", "Selector", nestedSelection, [nestedSelection]),
            ],
        };
        FakeHealthStorage health = new() { LatencyMilliseconds = 19 };
        TrayStatusService service = new(runtime, health, text => text);

        TrayStatusSnapshot snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(expectedNode, snapshot.CurrentNodeName);
        Assert.Equal(expectedNode.Length == 0 ? null : expectedNode, health.RequestedNodeName);
    }

    /// <summary>Verifies a missing active group never falls back to an inactive mode's selection.</summary>
    [Theory]
    [InlineData(ClashSharpMode.FullTakeover, "Proxy")]
    [InlineData(ClashSharpMode.RuleTakeover, "GLOBAL")]
    public async Task GetSnapshotAsync_MissingActiveGroupReturnsUnavailable(ClashSharpMode mode, string groupName)
    {
        TrayStatusService service = new(
            new FakeRuntime { Mode = mode, Groups = [new(groupName, "Selector", "Wrong node", ["Wrong node"])] },
            new FakeHealthStorage(),
            text => text);

        Assert.Equal(TrayStatusSnapshot.Unavailable, await service.GetSnapshotAsync(CancellationToken.None));
    }

    /// <summary>Verifies a controller read spanning a new mode or generation is discarded.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeAdapter_RejectsConcurrentTransition(bool changeMode)
    {
        RuntimeConfigurationIntegrityObservation state = ActiveRuntime(ClashSharpMode.FullTakeover);
        TrayStatusRuntimeAdapter adapter = new(
            _ =>
            {
                state = changeMode
                    ? ActiveRuntime(ClashSharpMode.RuleTakeover)
                    : state with { AppliedGeneration = 2 };
                return Task.FromResult<IReadOnlyList<MihomoProxyGroup>>([]);
            },
            () => state);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetSnapshotAsync(CancellationToken.None));
    }

    /// <summary>Verifies converged generation reads supply their applied mode and preserve caller cancellation.</summary>
    [Fact]
    public async Task RuntimeAdapter_ReadsStableGenerationAndRejectsIgnoredCancellation()
    {
        using CancellationTokenSource cancellation = new();
        bool cancelDuringRead = false;
        TrayStatusRuntimeAdapter adapter = new(
            _ =>
            {
                if (cancelDuringRead) { cancellation.Cancel(); }
                return Task.FromResult<IReadOnlyList<MihomoProxyGroup>>(
                    [new("GLOBAL", "Selector", "Node", ["Node"])]);
            },
            () => ActiveRuntime(ClashSharpMode.FullTakeover));

        TrayStatusRuntimeSnapshot snapshot = await adapter.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(ClashSharpMode.FullTakeover, snapshot.Mode);
        Assert.Equal("Node", Assert.Single(snapshot.Groups).CurrentSelection);
        cancelDuringRead = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetSnapshotAsync(cancellation.Token));
    }

    /// <summary>Verifies unknown state fails before I/O and inactive state does not query a stale controller.</summary>
    [Fact]
    public async Task RuntimeAdapter_UnknownOrInactiveDoesNotQueryController()
    {
        RuntimeConfigurationIntegrityObservation state = RuntimeConfigurationIntegrityObservation.Unknown;
        TrayStatusRuntimeAdapter adapter = new(
            _ => throw new Xunit.Sdk.XunitException("Controller must not be queried."),
            () => state);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetSnapshotAsync(CancellationToken.None));
        state = RuntimeConfigurationIntegrityObservation.Inactive;
        TrayStatusRuntimeSnapshot snapshot = await adapter.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(ClashSharpMode.Disabled, snapshot.Mode);
        Assert.Empty(snapshot.Groups);
    }

    private static RuntimeConfigurationIntegrityObservation ActiveRuntime(ClashSharpMode mode) =>
        new(true, new(mode, false, 10000, "test-profile"), 1, new string('a', 64));

    private sealed class FakeRuntime : ITrayStatusRuntime
    {
        public ClashSharpMode Mode { get; init; } = ClashSharpMode.RuleTakeover;

        public IReadOnlyList<MihomoProxyGroup> Groups { get; init; } = [];

        public Task<TrayStatusRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TrayStatusRuntimeSnapshot(Mode, Groups));
        }
    }

    private sealed class ThrowingRuntime : ITrayStatusRuntime
    {
        public Task<TrayStatusRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<TrayStatusRuntimeSnapshot>(
                new InvalidOperationException("runtime unavailable"));
        }
    }

    private sealed class CancelledRuntime : ITrayStatusRuntime
    {
        public Task<TrayStatusRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            return Task.FromCanceled<TrayStatusRuntimeSnapshot>(cancellationToken);
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
