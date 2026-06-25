/*
 * Proxy Latency Service Factory
 * Wires production dependencies for proxy node latency probing
 *
 * @author: WaterRun
 * @file: Service/ProxyLatencyServiceFactory.cs
 * @date: 2026-06-25
 */

using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Service;

public sealed partial class ProxyLatencyService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProxyLatencyService"/> instance.</value>
    public static ProxyLatencyService Instance { get; } = ProxyLatencyServiceFactory.CreateDefault();
}

/// <summary>Creates proxy latency services with production dependencies.</summary>
internal static class ProxyLatencyServiceFactory
{
    /// <summary>Creates the default proxy latency service used by the proxy page.</summary>
    public static ProxyLatencyService CreateDefault()
    {
        return new ProxyLatencyService(
            new ProxyLatencyStorageAdapter(LogStorageService.Instance),
            new TcpProxyLatencyProbe());
    }
}

internal sealed class ProxyLatencyStorageAdapter(LogStorageService logStorage) : IProxyLatencyStorage
{
    public void UpsertNodeHealth(string name, string regionCode, int? latencyMilliseconds)
    {
        logStorage.UpsertNodeHealth(name, regionCode, latencyMilliseconds);
    }
}

internal sealed class TcpProxyLatencyProbe : IProxyLatencyProbe
{
    public async Task<int?> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }
}
