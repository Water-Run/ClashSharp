using System.Net.Http;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for connection sampling orchestration.</summary>
public sealed class ConnectionSamplingServiceTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task FlushAsync_CapturesEnabledTrafficWithoutWaitingForTheLoopInterval(bool enabled, int expectedReads)
    {
        FakeConnectionSamplingSource source = new() { DownloadTotal = 245760 };
        FakeConnectionSamplingStorage storage = new();
        ConnectionSamplingService service = CreateService(
            new FakeConnectionSamplingSettings { IsEnabled = enabled, IntervalSeconds = 60 }, source, storage);
        await service.FlushAsync(CancellationToken.None);
        Assert.Equal(expectedReads, source.CallCount);
        Assert.Equal(expectedReads, storage.Snapshots.Count);
        if (enabled) { Assert.Equal(245760, storage.Snapshots[0].DownloadTotalBytes); }
    }

    [Fact]
    public async Task FlushAsync_SerializesWithAnInFlightBackgroundSample()
    {
        FakeConnectionSamplingSource source = new() { BlockFirstSample = true };
        ConnectionSamplingService service = CreateService(source: source);
        Task first = service.SampleOnceAsync(CancellationToken.None);
        await source.FirstSampleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task flush = service.FlushAsync(CancellationToken.None);
        Assert.False(flush.IsCompleted);
        Assert.Equal(1, source.CallCount);
        source.ReleaseFirstSample.TrySetResult(null);
        await Task.WhenAll(first, flush);
        Assert.Equal(2, source.CallCount);
    }

    /// <summary>Verifies disabled sampling settings prevent the background loop from starting.</summary>
    [Fact]
    public async Task StartAsync_WhenDisabled_DoesNotStart()
    {
        ConnectionSamplingService service = CreateService(new FakeConnectionSamplingSettings { IsEnabled = false });

        await service.StartAsync(CancellationToken.None);

        Assert.False(service.IsRunning);
        Assert.Equal(SupervisorHealthState.Stopped, service.Health.State);
    }

    /// <summary>Verifies lifecycle quiescence blocks replacement loops until the prior state is resumed.</summary>
    [Fact]
    public async Task QuiesceAsync_BlocksStartsUntilResumeRestoresPriorRunningState()
    {
        ConnectionSamplingService service = CreateService();
        await service.StartAsync(CancellationToken.None);

        QuiescedState priorState = await service.QuiesceAsync(CancellationToken.None);

        Assert.True(priorState.WasRunning);
        Assert.False(service.IsRunning);
        Assert.Equal(SupervisorHealthState.Stopped, service.Health.State);

        await service.ResumeAsync(priorState, CancellationToken.None);
        Assert.True(service.IsRunning);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies source failures are supervised, categorized, and logged without terminating the loop.</summary>
    [Fact]
    public async Task StartAsync_WhenSourceFails_PublishesRetryHealthAndStableWarning()
    {
        ControlledSupervisorClock clock = new();
        FakeConnectionSamplingStorage storage = new();
        FakeConnectionSamplingSource source = new()
        {
            Exception = new HttpRequestException("controller unavailable"),
        };
        ConnectionSamplingService service = CreateService(source: source, storage: storage, clock: clock);

        await service.StartAsync(CancellationToken.None);
        ControlledDelay initialDelay = await clock.TakeDelayAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), initialDelay.Duration);
        initialDelay.Complete();
        ControlledDelay retry = await clock.TakeDelayAsync();

        ConnectionSamplingLogEntry entry = Assert.Single(storage.Logs);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("ConnectionSampling", entry.Category);
        Assert.Equal("localized failed", entry.Message);
        Assert.Equal("supervisor.http", entry.Detail);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Duration);
        Assert.Equal(SupervisorHealthState.Retrying, service.Health.State);
        Assert.True(service.IsRunning);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies recovery is logged after a successful supervised probe.</summary>
    [Fact]
    public async Task StartAsync_WhenFailureRecovers_LogsRecovery()
    {
        ControlledSupervisorClock clock = new();
        FakeConnectionSamplingStorage storage = new()
        {
            InsertedCount = 12,
        };
        FakeConnectionSamplingSource source = new()
        {
            Exception = new HttpRequestException("controller unavailable"),
        };
        ConnectionSamplingService service = CreateService(source: source, storage: storage, clock: clock);

        await service.StartAsync(CancellationToken.None);
        ControlledDelay initialDelay = await clock.TakeDelayAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), initialDelay.Duration);
        initialDelay.Complete();
        ControlledDelay retry = await clock.TakeDelayAsync();
        source.Exception = null;
        retry.Complete();
        _ = await clock.TakeDelayAsync();

        Assert.Equal(2, storage.Logs.Count);
        Assert.Equal("localized failed", storage.Logs[0].Message);
        Assert.Equal("localized recovered", storage.Logs[1].Message);
        Assert.Equal("12 rows", storage.Logs[1].Detail);
        Assert.Equal(SupervisorHealthState.Recovering, service.Health.State);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies SQLite persistence failures participate in supervisor backoff.</summary>
    [Fact]
    public async Task StartAsync_WhenStorageFails_PublishesSqliteHealthAndRetries()
    {
        ControlledSupervisorClock clock = new();
        FakeConnectionSamplingStorage storage = new()
        {
            Exception = new SqliteException("database busy", 5),
        };
        FakeConnectionSamplingSource source = new()
        {
            Connections = [CreateConnection("connection-1", 100, 200)],
        };
        ConnectionSamplingService service = CreateService(source: source, storage: storage, clock: clock);

        await service.StartAsync(CancellationToken.None);
        ControlledDelay initialDelay = await clock.TakeDelayAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), initialDelay.Duration);
        initialDelay.Complete();
        ControlledDelay retry = await clock.TakeDelayAsync();

        Assert.Equal("supervisor.sqlite", service.Health.ErrorCode);
        Assert.Equal(SupervisorHealthState.Retrying, service.Health.State);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Duration);

        storage.Exception = null;
        retry.Complete();
        _ = await clock.TakeDelayAsync();

        ActiveConnection recoveredDelta = Assert.Single(Assert.Single(storage.Snapshots).Connections);
        Assert.Equal(100, recoveredDelta.UploadBytes);
        Assert.Equal(200, recoveredDelta.DownloadBytes);
        Assert.Equal(SupervisorHealthState.Recovering, service.Health.State);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>Preserves closed-connection traffic for the repository's atomic delta calculation.</summary>
    [Fact]
    public async Task SampleOnceAsync_WhenConnectionsClose_PassesTotalsWithoutReconstructingThemFromRows()
    {
        FakeConnectionSamplingStorage storage = new();
        FakeConnectionSamplingSource source = new()
        {
            Connections = [CreateConnection("connection-1", 100, 200)],
        };
        ConnectionSamplingService service = CreateService(source: source, storage: storage);

        await service.SampleOnceAsync(CancellationToken.None);
        source.Connections = [];
        source.UploadTotal = 150;
        source.DownloadTotal = 245760;
        await service.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(2, storage.Snapshots.Count);
        ActiveConnection firstDelta = Assert.Single(storage.Snapshots[0].Connections);
        Assert.Equal(100, firstDelta.UploadBytes);
        Assert.Equal(200, firstDelta.DownloadBytes);
        Assert.Empty(storage.Snapshots[1].Connections);
        Assert.Equal(150, storage.Snapshots[1].UploadTotalBytes);
        Assert.Equal(245760, storage.Snapshots[1].DownloadTotalBytes);
        Assert.Equal(storage.Snapshots[0].Epoch, storage.Snapshots[1].Epoch);
    }

    /// <summary>Verifies restart does not start a replacement sampling loop while the previous loop is still in-flight.</summary>
    [Fact]
    public async Task RestartFromSettings_WhenSampleIsInFlight_WaitsForPreviousLoopBeforeStartingReplacement()
    {
        FakeConnectionSamplingSettings settings = new() { IsEnabled = true, IntervalSeconds = 0 };
        FakeConnectionSamplingSource source = new()
        {
            BlockFirstSample = true,
        };
        ConnectionSamplingService service = CreateService(settings, source);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await source.FirstSampleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Task restarting = service.RestartFromSettingsAsync(CancellationToken.None);
            await Task.Delay(80);

            Assert.Equal(1, source.CallCount);
            Assert.False(restarting.IsCompleted);

            source.ReleaseFirstSample.TrySetResult(null);
            await restarting;
            await WaitUntilAsync(() => source.CallCount >= 2);
        }
        finally
        {
            source.ReleaseFirstSample.TrySetResult(null);
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ConnectionSamplingService CreateService(
        FakeConnectionSamplingSettings? settings = null,
        FakeConnectionSamplingSource? source = null,
        FakeConnectionSamplingStorage? storage = null,
        ISupervisorClock? clock = null)
    {
        return new ConnectionSamplingService(
            settings ?? new FakeConnectionSamplingSettings { IsEnabled = true, IntervalSeconds = 60 },
            source ?? new FakeConnectionSamplingSource(),
            storage ?? new FakeConnectionSamplingStorage(),
            key => key switch
            {
                "ConnectionSampling.Failed" => "localized failed",
                "ConnectionSampling.Recovered" => "localized recovered",
                "ConnectionSampling.RecoveredDetail.Format" => "{0:N0} rows",
                _ => key,
            },
            clock,
            new SupervisorBackoffPolicy(() => 0d));
    }

    private static ActiveConnection CreateConnection(string id, long uploadBytes, long downloadBytes)
    {
        return new ActiveConnection(
            id,
            "process.exe",
            "example.com",
            "MATCH",
            string.Empty,
            "DIRECT",
            uploadBytes,
            downloadBytes,
            DateTimeOffset.UnixEpoch);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeConnectionSamplingSettings : IConnectionSamplingSettings
    {
        public bool IsEnabled { get; set; }

        public int IntervalSeconds { get; set; } = 60;
    }

    private sealed class FakeConnectionSamplingSource : IConnectionSamplingSource
    {
        private readonly Guid _epoch = Guid.NewGuid();
        public long? UploadTotal { get; set; }
        public long? DownloadTotal { get; set; }
        public Exception? Exception { get; set; }

        public IReadOnlyList<ActiveConnection> Connections { get; set; } = [];

        public bool BlockFirstSample { get; init; }

        public int CallCount { get; private set; }

        public TaskCompletionSource<object?> FirstSampleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> ReleaseFirstSample { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MihomoTrafficSnapshot> GetTrafficSnapshotAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                FirstSampleStarted.TrySetResult(null);
                if (BlockFirstSample)
                {
                    await ReleaseFirstSample.Task;
                }
            }

            if (Exception is not null)
            {
                throw Exception;
            }

            return new MihomoTrafficSnapshot(_epoch,
                UploadTotal ?? Connections.Sum(row => row.UploadBytes),
                DownloadTotal ?? Connections.Sum(row => row.DownloadBytes), Connections);
        }
    }

    private sealed class FakeConnectionSamplingStorage : IConnectionSamplingStorage
    {
        public int InsertedCount { get; init; }

        public Exception? Exception { get; set; }

        public List<ConnectionSamplingLogEntry> Logs { get; } = [];

        public List<MihomoTrafficSnapshot> Snapshots { get; } = [];

        public int AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            Snapshots.Add(snapshot);
            return InsertedCount;
        }

        public void AppendLog(string level, string category, string message, string? detail)
        {
            Logs.Add(new ConnectionSamplingLogEntry(level, category, message, detail));
        }
    }

    private sealed class ControlledSupervisorClock : ISupervisorClock
    {
        private readonly SemaphoreSlim _available = new(0);
        private readonly Queue<ControlledDelay> _delays = new();
        private readonly object _sync = new();

        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            ControlledDelay controlled = new(delay, Advance, cancellationToken);
            lock (_sync)
            {
                _delays.Enqueue(controlled);
            }

            _available.Release();
            return controlled.Task;
        }

        public async Task<ControlledDelay> TakeDelayAsync()
        {
            bool available = await _available.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(available, "A supervised delay was not scheduled before the test timeout.");
            lock (_sync)
            {
                return _delays.Dequeue();
            }
        }

        private void Advance(TimeSpan duration)
        {
            UtcNow += duration;
        }
    }

    private sealed class ControlledDelay
    {
        private readonly TimeSpan _duration;
        private readonly Action<TimeSpan> _advance;
        private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;

        public ControlledDelay(TimeSpan duration, Action<TimeSpan> advance, CancellationToken cancellationToken)
        {
            _duration = duration;
            _advance = advance;
            _cancellationRegistration = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
        }

        public TimeSpan Duration => _duration;

        public Task Task => _completion.Task;

        public void Complete()
        {
            _advance(_duration);
            _cancellationRegistration.Dispose();
            _completion.TrySetResult(null);
        }
    }

    private readonly record struct ConnectionSamplingLogEntry(string Level, string Category, string Message, string? Detail);
}
