/*
 * Proxy Latency Service
 * Measures proxy node TCP reachability and records node health statistics
 *
 * @author: WaterRun
 * @file: Service/ProxyLatencyService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Persists measured proxy node health.</summary>
internal interface IProxyLatencyStorage
{
    /// <summary>Upserts node health for a measured node.</summary>
    void UpsertNodeHealth(string name, string regionCode, int? latencyMilliseconds);
}

/// <summary>Measures one TCP endpoint latency.</summary>
internal interface IProxyLatencyProbe
{
    /// <summary>Measures a TCP endpoint latency in milliseconds.</summary>
    Task<int?> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Measures proxy node TCP reachability and records node health statistics.</summary>
/// <remarks>
/// Invariants: Probes never mutate proxy configuration or Windows proxy settings.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: Opens short-lived TCP connections and writes node health rows to SQLite.
/// </remarks>
public sealed partial class ProxyLatencyService
{
    /// <summary>Per-node TCP connect timeout.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IProxyLatencyStorage _storage;

    private readonly IProxyLatencyProbe _probe;

    /// <summary>Initializes the latency service.</summary>
    internal ProxyLatencyService(IProxyLatencyStorage storage, IProxyLatencyProbe probe)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>Tests all provided nodes and records health rows for each node.</summary>
    /// <param name="nodes">Nodes to test. Must not be null.</param>
    /// <param name="cancellationToken">Cancels remaining latency tests.</param>
    /// <returns>Node rows with updated latency values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> is null.</exception>
    public async Task<IReadOnlyList<ProxyNode>> TestNodesAsync(IReadOnlyList<ProxyNode> nodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        List<ProxyNode> testedNodes = new(nodes.Count);
        foreach (ProxyNode node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? latency = await TestNodeAsync(node, cancellationToken).ConfigureAwait(false);
            ProxyNode testedNode = node with { LatencyMilliseconds = latency };
            testedNodes.Add(testedNode);
            _storage.UpsertNodeHealth(testedNode.Name, testedNode.Region.RegionCode, latency);
        }

        return testedNodes;
    }

    /// <summary>Tests one proxy node TCP endpoint.</summary>
    /// <param name="node">Node to test.</param>
    /// <param name="cancellationToken">Cancels the latency test.</param>
    /// <returns>Measured latency in milliseconds; null when no endpoint is available or the probe fails.</returns>
    private Task<int?> TestNodeAsync(ProxyNode node, CancellationToken cancellationToken)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(node.Protocol, "DIRECT"))
        {
            return Task.FromResult<int?>(0);
        }

        if (string.IsNullOrWhiteSpace(node.ServerHost) || node.ServerPort is null)
        {
            return Task.FromResult<int?>(null);
        }

        return _probe.ProbeAsync(node.ServerHost, node.ServerPort.Value, ProbeTimeout, cancellationToken);
    }
}
