using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides connection sampling settings.</summary>
internal interface IConnectionSamplingSettings
{
    /// <summary>Gets whether background connection sampling is enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>Gets the sampling loop interval in seconds.</summary>
    int IntervalSeconds { get; }
}

/// <summary>Reads active mihomo connections for sampling.</summary>
internal interface IConnectionSamplingSource
{
    /// <summary>Returns current active connections.</summary>
    Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken);
}

/// <summary>Persists sampled connection snapshots and sampling logs.</summary>
internal interface IConnectionSamplingStorage
{
    /// <summary>Appends one connection snapshot and returns inserted row count.</summary>
    int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections);

    /// <summary>Appends a sampling log entry.</summary>
    void AppendLog(string level, string category, string message, string? detail);
}

/// <summary>Periodically reads mihomo active connections and writes SQLite statistics.</summary>
/// <remarks>
/// Invariants: Only one sampling loop can run for this service instance.
/// Thread safety: Lifecycle transitions serialize through the injected supervisor.
/// Side effects: Performs local mihomo API requests and writes connection snapshots to SQLite.
/// </remarks>
public sealed partial class ConnectionSamplingService : IRuntimeParticipant
{
    /// <summary>Synchronization object guarding cumulative connection counters.</summary>
    private readonly object _counterLock = new();

    private readonly IConnectionSamplingSettings _settings;

    private readonly IConnectionSamplingSource _source;

    private readonly IConnectionSamplingStorage _storage;

    private readonly Func<string, string> _getString;

    /// <summary>Last observed cumulative byte counters keyed by stable active connection identity.</summary>
    private readonly Dictionary<string, ConnectionSampleCounters> _lastCountersByConnection = new(StringComparer.Ordinal);

    private readonly SupervisedLoop _supervisor;

    private SupervisorHealthState _lastLoggedHealthState = SupervisorHealthState.Stopped;

    private int _lastInsertedCount;

    /// <summary>Initializes the connection sampling service.</summary>
    internal ConnectionSamplingService(
        IConnectionSamplingSettings settings,
        IConnectionSamplingSource source,
        IConnectionSamplingStorage storage,
        Func<string, string> getString,
        ISupervisorClock? clock = null,
        SupervisorBackoffPolicy? backoff = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _supervisor = new SupervisedLoop(
            "connection-sampling",
            SampleOnceAsync,
            GetSamplingInterval,
            clock ?? SystemSupervisorClock.Instance,
            backoff ?? SupervisorBackoffPolicy.CreateProduction("connection-sampling"),
            healthChanged: OnHealthChanged,
            initialDelay: GetSamplingInterval);
    }

    /// <inheritdoc />
    public string Name => _supervisor.Name;

    /// <summary>Gets the latest sampling supervisor health snapshot.</summary>
    public SupervisorHealth Health => _supervisor.Health;

    /// <summary>Gets whether the background sampling loop is currently running.</summary>
    /// <value>True when the loop is active; otherwise false.</value>
    public bool IsRunning => _supervisor.IsRunning;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.IsEnabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        return _supervisor.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
    {
        return _supervisor.QuiesceAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
    {
        if (!_settings.IsEnabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        return _supervisor.ResumeAsync(priorState, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _supervisor.StopAsync(cancellationToken);
    }

    /// <summary>Re-evaluates current settings through an awaited stop-and-start transition.</summary>
    public async Task RestartFromSettingsAsync(CancellationToken cancellationToken)
    {
        await _supervisor.QuiesceAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.IsEnabled)
        {
            await _supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Samples active connections once and writes them to SQLite.</summary>
    /// <param name="cancellationToken">Cancels the sample.</param>
    internal async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveConnection> connections = await _source
            .GetActiveConnectionsAsync(cancellationToken)
            .ConfigureAwait(false);
        ConnectionSamplePlan plan = CreateSamplePlan(connections);
        int insertedCount = _storage.AppendConnectionSnapshot(plan.DeltaConnections);
        CommitCounters(plan.NextCounters);
        _lastInsertedCount = insertedCount;
    }

    /// <summary>Builds deltas without advancing counters until persistence succeeds.</summary>
    private ConnectionSamplePlan CreateSamplePlan(IReadOnlyList<ActiveConnection> connections)
    {
        List<ActiveConnection> deltaConnections = [];
        Dictionary<string, ConnectionSampleCounters> nextCounters = new(StringComparer.Ordinal);

        lock (_counterLock)
        {
            foreach (ActiveConnection connection in connections)
            {
                string key = BuildConnectionKey(connection);

                long uploadDelta = connection.UploadBytes;
                long downloadDelta = connection.DownloadBytes;
                if (_lastCountersByConnection.TryGetValue(key, out ConnectionSampleCounters previousCounters))
                {
                    uploadDelta = connection.UploadBytes >= previousCounters.UploadBytes
                        ? connection.UploadBytes - previousCounters.UploadBytes
                        : connection.UploadBytes;
                    downloadDelta = connection.DownloadBytes >= previousCounters.DownloadBytes
                        ? connection.DownloadBytes - previousCounters.DownloadBytes
                        : connection.DownloadBytes;
                }

                nextCounters[key] = new ConnectionSampleCounters(connection.UploadBytes, connection.DownloadBytes);
                if (uploadDelta > 0 || downloadDelta > 0)
                {
                    deltaConnections.Add(connection with
                    {
                        UploadBytes = Math.Max(0, uploadDelta),
                        DownloadBytes = Math.Max(0, downloadDelta),
                    });
                }
            }
        }

        return new ConnectionSamplePlan(deltaConnections, nextCounters);
    }

    private void CommitCounters(IReadOnlyDictionary<string, ConnectionSampleCounters> nextCounters)
    {
        lock (_counterLock)
        {
            _lastCountersByConnection.Clear();
            foreach ((string key, ConnectionSampleCounters counters) in nextCounters)
            {
                _lastCountersByConnection.Add(key, counters);
            }
        }
    }

    private static string BuildConnectionKey(ActiveConnection connection)
    {
        return $"{connection.Id}|{connection.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
    }

    private string GetString(string key)
    {
        return _getString(key);
    }

    private string FormatString(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, GetString(key), args);
    }

    private TimeSpan GetSamplingInterval()
    {
        return TimeSpan.FromSeconds(Math.Max(0, _settings.IntervalSeconds));
    }

    private void OnHealthChanged(SupervisorHealth health)
    {
        SupervisorHealthState previous = _lastLoggedHealthState;
        _lastLoggedHealthState = health.State;
        if (health.State is SupervisorHealthState.Retrying or SupervisorHealthState.Degraded
            && previous is not SupervisorHealthState.Retrying and not SupervisorHealthState.Degraded)
        {
            _storage.AppendLog(
                "Warning",
                "ConnectionSampling",
                GetString("ConnectionSampling.Failed"),
                health.ErrorCode);
            return;
        }

        if (health.State == SupervisorHealthState.Recovering
            && previous is SupervisorHealthState.Retrying or SupervisorHealthState.Degraded)
        {
            _storage.AppendLog(
                "Info",
                "ConnectionSampling",
                GetString("ConnectionSampling.Recovered"),
                FormatString("ConnectionSampling.RecoveredDetail.Format", _lastInsertedCount));
        }
    }

    private readonly record struct ConnectionSampleCounters(long UploadBytes, long DownloadBytes);

    private sealed record ConnectionSamplePlan(
        IReadOnlyList<ActiveConnection> DeltaConnections,
        IReadOnlyDictionary<string, ConnectionSampleCounters> NextCounters);
}
