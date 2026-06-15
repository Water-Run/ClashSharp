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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Periodically reads mihomo active connections and writes SQLite statistics.</summary>
/// <remarks>
/// Invariants: Only one sampling loop can run for this service instance.
/// Thread safety: Start and stop operations serialize state through a private lock.
/// Side effects: Performs local mihomo API requests and writes connection snapshots to SQLite.
/// </remarks>
public sealed class ConnectionSamplingService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ConnectionSamplingService"/> instance.</value>
    public static ConnectionSamplingService Instance { get; } = new();

    /// <summary>Synchronization object guarding service lifetime state.</summary>
    private readonly object _syncLock = new();

    /// <summary>Cancellation source for the running sampling loop.</summary>
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>Background sampling task.</summary>
    private Task? _samplingTask;

    /// <summary>Tracks whether the previous sample failed so repeated failures do not flood logs.</summary>
    private bool _lastSampleFailed;

    /// <summary>Initializes the connection sampling service.</summary>
    private ConnectionSamplingService()
    {
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
        if (!AppSettingsService.Instance.ConnectionSamplingEnabled)
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
            TimeSpan interval = TimeSpan.FromSeconds(AppSettingsService.Instance.ConnectionSamplingIntervalSeconds);
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
    private async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ActiveConnection> connections = await MihomoConnectionService.Instance.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
            int insertedCount = LogStorageService.Instance.AppendConnectionSnapshot(connections);
            if (_lastSampleFailed)
            {
                LogStorageService.Instance.AppendLog("Info", "ConnectionSampling", "Background connection sampling recovered.", $"{insertedCount:N0} rows.");
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
                LogStorageService.Instance.AppendLog("Warning", "ConnectionSampling", "Background connection sampling failed.", exception.Message);
            }

            _lastSampleFailed = true;
        }
    }
}
