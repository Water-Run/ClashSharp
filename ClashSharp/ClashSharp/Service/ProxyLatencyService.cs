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
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Measures proxy node TCP reachability and records node health statistics.</summary>
/// <remarks>
/// Invariants: Probes never mutate proxy configuration or Windows proxy settings.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: Opens short-lived TCP connections and writes node health rows to SQLite.
/// </remarks>
public sealed class ProxyLatencyService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProxyLatencyService"/> instance.</value>
    public static ProxyLatencyService Instance { get; } = new();

    /// <summary>Per-node TCP connect timeout.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Initializes the latency service.</summary>
    private ProxyLatencyService()
    {
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
            LogStorageService.Instance.UpsertNodeHealth(testedNode.Name, testedNode.Region.RegionCode, latency);
        }

        return testedNodes;
    }

    /// <summary>Tests one proxy node TCP endpoint.</summary>
    /// <param name="node">Node to test.</param>
    /// <param name="cancellationToken">Cancels the latency test.</param>
    /// <returns>Measured latency in milliseconds; null when no endpoint is available or the probe fails.</returns>
    private static async Task<int?> TestNodeAsync(ProxyNode node, CancellationToken cancellationToken)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(node.Protocol, "DIRECT"))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(node.ServerHost) || node.ServerPort is null)
        {
            return null;
        }

        using TcpClient client = new();
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(node.ServerHost, node.ServerPort.Value, timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }
}
