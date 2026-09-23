using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Reads process-wide traffic counters and active connections.</summary>
internal interface IRuntimeTrafficConnections
{
    /// <summary>Gets cumulative counters, including closed connections, from the current core.</summary>
    Task<MihomoTrafficSnapshot> GetTrafficSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Calculates upload and download rates from successive core-wide traffic counters.</summary>
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
    private readonly SemaphoreSlim _samplingGate = new(1, 1);
    private MihomoTrafficSnapshot? _lastCounters;
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

    public Task<RuntimeTrafficRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        GetSnapshotCoreAsync(force: false, cancellationToken);

    /// <summary>Reads even a recently sampled controller before a planned core transition.</summary>
    internal Task<RuntimeTrafficRateSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
        GetSnapshotCoreAsync(force: true, cancellationToken);

    private async Task<RuntimeTrafficRateSnapshot> GetSnapshotCoreAsync(bool force, CancellationToken cancellationToken)
    {
        // Dashboard and trigger reads share one app-session counter history. Coalesce overlapping
        // reads so a second consumer cannot turn the same sample into a spurious zero-rate sample.
        await _samplingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_syncLock)
            {
                TimeSpan age = _lastSampledAt is DateTimeOffset sampledAt
                    ? _getNow() - sampledAt
                    : TimeSpan.MaxValue;
                if (!force && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(1))
                {
                    return _latestSnapshot;
                }
            }

            return await SampleAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _samplingGate.Release();
        }
    }

    private async Task<RuntimeTrafficRateSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        MihomoTrafficSnapshot counters = await _connections.GetTrafficSnapshotAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset sampledAt = _getNow();

        lock (_syncLock)
        {
            if (_lastSampledAt is null)
            {
                _lastSampledAt = sampledAt;
                _lastCounters = counters;
                // The core may have handled short requests before any page requested its first sample.
                _latestSnapshot = new RuntimeTrafficRateSnapshot(0, 0, counters.Connections.Count,
                    counters.UploadTotalBytes, counters.DownloadTotalBytes);
                return _latestSnapshot;
            }

            double seconds = Math.Max(1, (sampledAt - _lastSampledAt.Value).TotalSeconds);
            bool sameEpoch = counters.Epoch == _lastCounters!.Epoch;
            long uploadDelta = GetDelta(counters.UploadTotalBytes, _lastCounters.UploadTotalBytes, sameEpoch);
            long downloadDelta = GetDelta(counters.DownloadTotalBytes, _lastCounters.DownloadTotalBytes, sameEpoch);

            _lastSampledAt = sampledAt;
            _lastCounters = counters;
            _latestSnapshot = new RuntimeTrafficRateSnapshot(
                GetRate(uploadDelta, seconds),
                GetRate(downloadDelta, seconds),
                counters.Connections.Count,
                AddSaturated(_latestSnapshot.SessionUploadBytes, uploadDelta),
                AddSaturated(_latestSnapshot.SessionDownloadBytes, downloadDelta));
            return _latestSnapshot;
        }
    }

    private static long GetDelta(long current, long previous, bool sameEpoch) =>
        sameEpoch && current >= previous ? current - previous : current;

    private static long AddSaturated(long total, long delta) =>
        total > long.MaxValue - delta ? long.MaxValue : total + delta;

    private static long GetRate(long delta, double seconds)
    {
        double rate = Math.Round(delta / seconds);
        return rate >= long.MaxValue ? long.MaxValue : (long)rate;
    }
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

    public Task<MihomoTrafficSnapshot> GetTrafficSnapshotAsync(CancellationToken cancellationToken)
    {
        return _connections.GetTrafficSnapshotAsync(cancellationToken);
    }
}
#endif
