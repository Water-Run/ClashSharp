using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Settings;
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

/// <summary>Reads cumulative mihomo traffic counters and current connections.</summary>
internal interface IConnectionSamplingSource
{
    /// <summary>Returns one snapshot bound to a core process counter epoch.</summary>
    Task<MihomoTrafficSnapshot> GetTrafficSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Persists sampled connection snapshots and sampling logs.</summary>
internal interface IConnectionSamplingStorage
{
    /// <summary>Commits counters and connection deltas atomically and returns inserted row count.</summary>
    int AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot);

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
    /// <summary>Prevents a transition flush and the background loop from committing samples out of order.</summary>
    private readonly SemaphoreSlim _sampleGate = new(1, 1);

    private readonly IConnectionSamplingSettings _settings;

    private readonly IConnectionSamplingSource _source;

    private readonly IConnectionSamplingStorage _storage;

    private readonly Func<string, string> _getString;

    private readonly SupervisedLoop _supervisor;

    /// <summary>Serializes complete configuration changes with ordinary lifecycle transitions.</summary>
    private readonly SemaphoreSlim _configurationGate = new(1, 1);

    private ConnectionSamplingSettings? _explicitSettings;

    /// <summary>Installed loop interval; never reads a desired preference during an iteration.</summary>
    private int _effectiveIntervalSeconds = 30;

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
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConfiguredEnabled) { await StartConfiguredLoopAsync(cancellationToken).ConfigureAwait(false); }
        }
        finally { _configurationGate.Release(); }
    }

    /// <inheritdoc />
    public async Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _supervisor.QuiesceAsync(cancellationToken).ConfigureAwait(false); }
        finally { _configurationGate.Release(); }
    }

    /// <inheritdoc />
    public async Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(priorState);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConfiguredEnabled && priorState.WasRunning)
            {
                if (!_supervisor.IsRunning) { InstallConfiguredInterval(); }
                await _supervisor.ResumeAsync(priorState, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _configurationGate.Release(); }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _supervisor.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { _configurationGate.Release(); }
    }

    /// <summary>Starts an admitted settings transaction's loop, including a running baseline with a disabled preference.</summary>
    internal async Task StartLoopAsync(CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StartConfiguredLoopAsync(cancellationToken).ConfigureAwait(false); }
        finally { _configurationGate.Release(); }
    }

    /// <summary>Re-evaluates current settings through an awaited stop-and-start transition.</summary>
    public async Task RestartFromSettingsAsync(CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _supervisor.QuiesceAsync(cancellationToken).ConfigureAwait(false);
            if (IsConfiguredEnabled) { await StartConfiguredLoopAsync(cancellationToken).ConfigureAwait(false); }
        }
        finally { _configurationGate.Release(); }
    }

    /// <summary>Samples active connections once and writes them to SQLite.</summary>
    /// <param name="cancellationToken">Cancels the sample.</param>
    internal async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MihomoTrafficSnapshot snapshot = await _source.GetTrafficSnapshotAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _lastInsertedCount = _storage.AppendTrafficSnapshot(snapshot);
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    /// <summary>Captures a final enabled sample before the controller is stopped or replaced.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConfiguredEnabled)
            {
                await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _configurationGate.Release();
        }
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
        return TimeSpan.FromSeconds(Math.Max(0, Volatile.Read(ref _effectiveIntervalSeconds)));
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

}
