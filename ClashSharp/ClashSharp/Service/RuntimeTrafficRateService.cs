/*
 * Runtime Traffic Rate Service
 * Calculates realtime upload and download rates from mihomo connection counters
 *
 * @author: WaterRun
 * @file: Service/RuntimeTrafficRateService.cs
 * @date: 2026-06-29
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Reads active connections for runtime traffic sampling.</summary>
internal interface IRuntimeTrafficConnections
{
    /// <summary>Gets current active connections.</summary>
    Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken);
}

/// <summary>Calculates upload and download rates from successive active connection snapshots.</summary>
internal sealed class RuntimeTrafficRateService
{
#if UNIT_TESTS
    public static RuntimeTrafficRateService Instance => throw new NotSupportedException("Use explicit RuntimeTrafficRateService dependencies in tests.");
#else
    public static RuntimeTrafficRateService Instance { get; } = new(new RuntimeTrafficConnectionsAdapter(MihomoConnectionService.Instance));
#endif

    private readonly IRuntimeTrafficConnections _connections;
    private readonly Func<DateTimeOffset> _getNow;
    private readonly object _syncLock = new();
    private Dictionary<string, ConnectionCounter> _lastCounters = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastSampledAt;
    private RuntimeTrafficRateSnapshot _latestSnapshot;

    public RuntimeTrafficRateService(IRuntimeTrafficConnections connections, Func<DateTimeOffset>? getNow = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
    }

    public RuntimeTrafficRateSnapshot GetLatestSnapshot()
    {
        lock (_syncLock)
        {
            return _latestSnapshot;
        }
    }

    public async Task<RuntimeTrafficRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveConnection> connections = await _connections.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset sampledAt = _getNow();
        Dictionary<string, ConnectionCounter> currentCounters = connections.ToDictionary(
            static connection => connection.Id,
            static connection => new ConnectionCounter(connection.UploadBytes, connection.DownloadBytes),
            StringComparer.Ordinal);

        lock (_syncLock)
        {
            if (_lastSampledAt is null)
            {
                _lastSampledAt = sampledAt;
                _lastCounters = currentCounters;
                _latestSnapshot = new RuntimeTrafficRateSnapshot(0, 0, connections.Count, 0, 0);
                return _latestSnapshot;
            }

            double seconds = Math.Max(1, (sampledAt - _lastSampledAt.Value).TotalSeconds);
            long uploadDelta = 0;
            long downloadDelta = 0;
            foreach ((string id, ConnectionCounter current) in currentCounters)
            {
                if (_lastCounters.TryGetValue(id, out ConnectionCounter previous))
                {
                    uploadDelta += current.UploadBytes >= previous.UploadBytes
                        ? current.UploadBytes - previous.UploadBytes
                        : current.UploadBytes;
                    downloadDelta += current.DownloadBytes >= previous.DownloadBytes
                        ? current.DownloadBytes - previous.DownloadBytes
                        : current.DownloadBytes;
                }
                else
                {
                    uploadDelta += current.UploadBytes;
                    downloadDelta += current.DownloadBytes;
                }
            }

            _lastSampledAt = sampledAt;
            _lastCounters = currentCounters;
            _latestSnapshot = new RuntimeTrafficRateSnapshot(
                (long)Math.Round(uploadDelta / seconds),
                (long)Math.Round(downloadDelta / seconds),
                connections.Count,
                _latestSnapshot.SessionUploadBytes + uploadDelta,
                _latestSnapshot.SessionDownloadBytes + downloadDelta);
            return _latestSnapshot;
        }
    }

    private readonly record struct ConnectionCounter(long UploadBytes, long DownloadBytes);
}

#if !UNIT_TESTS
/// <summary>Adapts mihomo connection service to realtime traffic sampling.</summary>
internal sealed class RuntimeTrafficConnectionsAdapter : IRuntimeTrafficConnections
{
    private readonly MihomoConnectionService _connections;

    public RuntimeTrafficConnectionsAdapter(MihomoConnectionService connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        return _connections.GetActiveConnectionsAsync(cancellationToken);
    }
}
#endif
