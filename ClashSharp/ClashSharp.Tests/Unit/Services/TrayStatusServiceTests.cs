/*
 * Tray Status Service Tests
 * Verifies system tray status snapshots summarize current runtime proxy information
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/TrayStatusServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for tray status snapshot construction.</summary>
public sealed class TrayStatusServiceTests
{
    /// <summary>Verifies the primary runtime proxy group contributes current node and health latency.</summary>
    [Fact]
    public void GetSnapshot_UsesPrimaryProxyGroupAndStoredLatency()
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

        TrayStatusSnapshot snapshot = service.GetSnapshot();

        Assert.Equal("display:Node A", snapshot.CurrentNodeName);
        Assert.Equal("Node A", healthStorage.RequestedNodeName);
        Assert.Equal(42, snapshot.LatencyMilliseconds);
    }

    /// <summary>Verifies runtime failures produce an unavailable status snapshot.</summary>
    [Fact]
    public void GetSnapshot_WhenRuntimeUnavailable_ReturnsUnavailable()
    {
        TrayStatusService service = new(new ThrowingRuntime(), new FakeHealthStorage(), text => text);

        TrayStatusSnapshot snapshot = service.GetSnapshot();

        Assert.Equal(TrayStatusSnapshot.Unavailable, snapshot);
    }

    private sealed class FakeRuntime : ITrayStatusRuntime
    {
        public IReadOnlyList<MihomoProxyGroup> Groups { get; init; } = [];

        public IReadOnlyList<MihomoProxyGroup> GetProxyGroups()
        {
            return Groups;
        }
    }

    private sealed class ThrowingRuntime : ITrayStatusRuntime
    {
        public IReadOnlyList<MihomoProxyGroup> GetProxyGroups()
        {
            throw new InvalidOperationException("runtime unavailable");
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
