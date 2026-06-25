/*
 * Connection Sampling Service
 * Periodically reads mihomo active connections and writes SQLite statistics
 *
 * @author: WaterRun
 * @file: Service/ConnectionSamplingService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
/// Thread safety: Start and stop operations serialize state through a private lock.
/// Side effects: Performs local mihomo API requests and writes connection snapshots to SQLite.
/// </remarks>
public sealed partial class ConnectionSamplingService
{
    /// <summary>Synchronization object guarding service lifetime state.</summary>
    private readonly object _syncLock = new();

    private readonly IConnectionSamplingSettings _settings;

    private readonly IConnectionSamplingSource _source;

    private readonly IConnectionSamplingStorage _storage;

    private readonly Func<string, string> _getString;

    /// <summary>Cancellation source for the running sampling loop.</summary>
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>Background sampling task.</summary>
    private Task? _samplingTask;

    /// <summary>Tracks whether the previous sample failed so repeated failures do not flood logs.</summary>
    private bool _lastSampleFailed;

    /// <summary>Initializes the connection sampling service.</summary>
    internal ConnectionSamplingService(
        IConnectionSamplingSettings settings,
        IConnectionSamplingSource source,
        IConnectionSamplingStorage storage,
        Func<string, string> getString)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Gets whether the background sampling loop is currently running.</summary>
    /// <value>True when the loop is active; otherwise false.</value>
    public bool IsRunning
    {
        get
        {
            lock (_syncLock)
            {
                return _samplingTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>Starts the background sampling loop when enabled by settings.</summary>
    public void StartIfEnabled()
    {
        if (!_settings.IsEnabled)
        {
            return;
        }

        Start();
    }

    /// <summary>Starts the background sampling loop.</summary>
    public void Start()
    {
        lock (_syncLock)
        {
            if (_samplingTask is { IsCompleted: false })
            {
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _samplingTask = Task.Run(() => RunSamplingLoopAsync(_cancellationTokenSource.Token));
        }
    }

    /// <summary>Stops the background sampling loop.</summary>
    public void Stop()
    {
        CancellationTokenSource? cancellationTokenSource;

        lock (_syncLock)
        {
            cancellationTokenSource = _cancellationTokenSource;
            _cancellationTokenSource = null;
            _samplingTask = null;
        }

        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
    }

    /// <summary>Restarts the sampling loop using current settings.</summary>
    public void RestartFromSettings()
    {
        Stop();
        StartIfEnabled();
    }

    /// <summary>Runs the sampling loop until canceled.</summary>
    /// <param name="cancellationToken">Loop cancellation token.</param>
    private async Task RunSamplingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan interval = TimeSpan.FromSeconds(_settings.IntervalSeconds);
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Samples active connections once and writes them to SQLite.</summary>
    /// <param name="cancellationToken">Cancels the sample.</param>
    internal async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ActiveConnection> connections = await _source.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
            int insertedCount = _storage.AppendConnectionSnapshot(connections);
            if (_lastSampleFailed)
            {
                _storage.AppendLog(
                    "Info",
                    "ConnectionSampling",
                    GetString("ConnectionSampling.Recovered"),
                    FormatString("ConnectionSampling.RecoveredDetail.Format", insertedCount));
            }

            _lastSampleFailed = false;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!_lastSampleFailed)
            {
                _storage.AppendLog("Warning", "ConnectionSampling", GetString("ConnectionSampling.Failed"), exception.Message);
            }

            _lastSampleFailed = true;
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
}
