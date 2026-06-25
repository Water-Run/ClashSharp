/*
 * Proxy Latency Service Tests
 * Verifies node latency orchestration through injected probe and storage dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/ProxyLatencyServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for proxy latency measurement orchestration.</summary>
public sealed class ProxyLatencyServiceTests
{
    /// <summary>Verifies DIRECT nodes are treated as zero-latency and do not require a TCP probe.</summary>
    [Fact]
    public async Task TestNodesAsync_WhenDirectNode_RecordsZeroLatencyWithoutProbe()
    {
        FakeProxyLatencyStorage storage = new();
        FakeProxyLatencyProbe probe = new();
        ProxyLatencyService service = new(storage, probe);
        ProxyNode node = CreateNode("Direct", "DIRECT", string.Empty, null);

        ProxyNode result = Assert.Single(await service.TestNodesAsync([node], CancellationToken.None));

        Assert.Equal(0, result.LatencyMilliseconds);
        Assert.Empty(probe.Requests);
        ProxyLatencyStorageEntry entry = Assert.Single(storage.Entries);
        Assert.Equal("Direct", entry.Name);
        Assert.Equal("CN", entry.RegionCode);
        Assert.Equal(0, entry.LatencyMilliseconds);
    }

    /// <summary>Verifies endpoint nodes are measured through the injected probe and recorded in storage.</summary>
    [Fact]
    public async Task TestNodesAsync_WhenEndpointNode_UsesProbeAndStoresLatency()
    {
        FakeProxyLatencyStorage storage = new();
        FakeProxyLatencyProbe probe = new()
        {
            LatencyMilliseconds = 42,
        };
        ProxyLatencyService service = new(storage, probe);
        ProxyNode node = CreateNode("HK", "SS", "hk.example.invalid", 443);

        ProxyNode result = Assert.Single(await service.TestNodesAsync([node], CancellationToken.None));

        Assert.Equal(42, result.LatencyMilliseconds);
        ProxyLatencyProbeRequest request = Assert.Single(probe.Requests);
        Assert.Equal("hk.example.invalid", request.Host);
        Assert.Equal(443, request.Port);
        Assert.Equal(TimeSpan.FromSeconds(3), request.Timeout);
        ProxyLatencyStorageEntry entry = Assert.Single(storage.Entries);
        Assert.Equal("HK", entry.Name);
        Assert.Equal("CN", entry.RegionCode);
        Assert.Equal(42, entry.LatencyMilliseconds);
    }

    /// <summary>Verifies nodes without an endpoint are recorded as unmeasurable without probing.</summary>
    [Fact]
    public async Task TestNodesAsync_WhenEndpointMissing_RecordsNullLatencyWithoutProbe()
    {
        FakeProxyLatencyStorage storage = new();
        FakeProxyLatencyProbe probe = new();
        ProxyLatencyService service = new(storage, probe);
        ProxyNode node = CreateNode("Provider", "PROVIDER/HTTP", string.Empty, null);

        ProxyNode result = Assert.Single(await service.TestNodesAsync([node], CancellationToken.None));

        Assert.Null(result.LatencyMilliseconds);
        Assert.Empty(probe.Requests);
        Assert.Null(Assert.Single(storage.Entries).LatencyMilliseconds);
    }

    private static ProxyNode CreateNode(string name, string protocol, string host, int? port)
    {
        return new ProxyNode(name, protocol, new RegionMetadata("CN", "China", "cn.png"), null, host, port);
    }

    private sealed class FakeProxyLatencyStorage : IProxyLatencyStorage
    {
        public List<ProxyLatencyStorageEntry> Entries { get; } = [];

        public void UpsertNodeHealth(string name, string regionCode, int? latencyMilliseconds)
        {
            Entries.Add(new ProxyLatencyStorageEntry(name, regionCode, latencyMilliseconds));
        }
    }

    private sealed class FakeProxyLatencyProbe : IProxyLatencyProbe
    {
        public int? LatencyMilliseconds { get; init; }

        public List<ProxyLatencyProbeRequest> Requests { get; } = [];

        public Task<int?> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(new ProxyLatencyProbeRequest(host, port, timeout));
            return Task.FromResult(LatencyMilliseconds);
        }
    }

    private readonly record struct ProxyLatencyStorageEntry(string Name, string RegionCode, int? LatencyMilliseconds);

    private readonly record struct ProxyLatencyProbeRequest(string Host, int Port, TimeSpan Timeout);
}
