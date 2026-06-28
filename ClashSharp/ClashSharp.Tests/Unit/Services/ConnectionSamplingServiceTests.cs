/*
 * Connection Sampling Service Tests
 * Verifies background connection sampling orchestration through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/ConnectionSamplingServiceTests.cs
 * @date: 2026-06-25
 */

using System.Net.Http;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for connection sampling orchestration.</summary>
public sealed class ConnectionSamplingServiceTests
{
    /// <summary>Verifies disabled sampling settings prevent the background loop from starting.</summary>
    [Fact]
    public void StartIfEnabled_WhenDisabled_DoesNotStart()
    {
        ConnectionSamplingService service = CreateService(new FakeConnectionSamplingSettings { IsEnabled = false });

        service.StartIfEnabled();

        Assert.False(service.IsRunning);
    }

    /// <summary>Verifies one failed sample logs one localized warning and repeated failures are suppressed.</summary>
    [Fact]
    public async Task SampleOnceAsync_WhenRepeatedFailures_LogsWarningOnce()
    {
        FakeConnectionSamplingStorage storage = new();
        FakeConnectionSamplingSource source = new()
        {
            Exception = new HttpRequestException("controller unavailable"),
        };
        ConnectionSamplingService service = CreateService(source: source, storage: storage);

        await service.SampleOnceAsync(CancellationToken.None);
        await service.SampleOnceAsync(CancellationToken.None);

        ConnectionSamplingLogEntry entry = Assert.Single(storage.Logs);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("ConnectionSampling", entry.Category);
        Assert.Equal("localized failed", entry.Message);
        Assert.Equal("controller unavailable", entry.Detail);
    }

    /// <summary>Verifies recovery after a failed sample logs a localized info message with inserted row count detail.</summary>
    [Fact]
    public async Task SampleOnceAsync_WhenFailureRecovers_LogsRecovery()
    {
        FakeConnectionSamplingStorage storage = new()
        {
            InsertedCount = 12,
        };
        FakeConnectionSamplingSource source = new()
        {
            Exception = new HttpRequestException("controller unavailable"),
        };
        ConnectionSamplingService service = CreateService(source: source, storage: storage);

        await service.SampleOnceAsync(CancellationToken.None);
        source.Exception = null;
        await service.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(2, storage.Logs.Count);
        Assert.Equal("localized failed", storage.Logs[0].Message);
        Assert.Equal("localized recovered", storage.Logs[1].Message);
        Assert.Equal("12 rows", storage.Logs[1].Detail);
    }

    /// <summary>Verifies cumulative mihomo byte counters are not persisted twice when a connection remains active.</summary>
    [Fact]
    public async Task SampleOnceAsync_WhenConnectionCountersAreUnchanged_PersistsOnlyInitialDelta()
    {
        FakeConnectionSamplingStorage storage = new();
        FakeConnectionSamplingSource source = new()
        {
            Connections = [CreateConnection("connection-1", 100, 200)],
        };
        ConnectionSamplingService service = CreateService(source: source, storage: storage);

        await service.SampleOnceAsync(CancellationToken.None);
        source.Connections = [CreateConnection("connection-1", 100, 200)];
        await service.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(2, storage.Snapshots.Count);
        ActiveConnection firstDelta = Assert.Single(storage.Snapshots[0]);
        Assert.Equal(100, firstDelta.UploadBytes);
        Assert.Equal(200, firstDelta.DownloadBytes);
        Assert.Empty(storage.Snapshots[1]);
    }

    /// <summary>Verifies repeated active connection samples persist only the byte increase after the first sample.</summary>
    [Fact]
    public async Task SampleOnceAsync_WhenConnectionCountersIncrease_PersistsOnlyCounterDelta()
    {
        FakeConnectionSamplingStorage storage = new();
        FakeConnectionSamplingSource source = new()
        {
            Connections = [CreateConnection("connection-1", 100, 200)],
        };
        ConnectionSamplingService service = CreateService(source: source, storage: storage);

        await service.SampleOnceAsync(CancellationToken.None);
        source.Connections = [CreateConnection("connection-1", 140, 260)];
        await service.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(2, storage.Snapshots.Count);
        ActiveConnection secondDelta = Assert.Single(storage.Snapshots[1]);
        Assert.Equal("connection-1", secondDelta.Id);
        Assert.Equal(40, secondDelta.UploadBytes);
        Assert.Equal(60, secondDelta.DownloadBytes);
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
            service.Start();
            await source.FirstSampleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            service.RestartFromSettings();
            await Task.Delay(80);

            Assert.Equal(1, source.CallCount);

            source.ReleaseFirstSample.TrySetResult(null);
            await WaitUntilAsync(() => source.CallCount >= 2);
        }
        finally
        {
            service.Stop();
            source.ReleaseFirstSample.TrySetResult(null);
        }
    }

    private static ConnectionSamplingService CreateService(
        FakeConnectionSamplingSettings? settings = null,
        FakeConnectionSamplingSource? source = null,
        FakeConnectionSamplingStorage? storage = null)
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
            });
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
        public bool IsEnabled { get; init; }

        public int IntervalSeconds { get; init; } = 60;
    }

    private sealed class FakeConnectionSamplingSource : IConnectionSamplingSource
    {
        public Exception? Exception { get; set; }

        public IReadOnlyList<ActiveConnection> Connections { get; set; } = [];

        public bool BlockFirstSample { get; init; }

        public int CallCount { get; private set; }

        public TaskCompletionSource<object?> FirstSampleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> ReleaseFirstSample { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
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

            return Connections;
        }
    }

    private sealed class FakeConnectionSamplingStorage : IConnectionSamplingStorage
    {
        public int InsertedCount { get; init; }

        public List<ConnectionSamplingLogEntry> Logs { get; } = [];

        public List<IReadOnlyList<ActiveConnection>> Snapshots { get; } = [];

        public int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections)
        {
            Snapshots.Add([.. connections]);
            return InsertedCount;
        }

        public void AppendLog(string level, string category, string message, string? detail)
        {
            Logs.Add(new ConnectionSamplingLogEntry(level, category, message, detail));
        }
    }

    private readonly record struct ConnectionSamplingLogEntry(string Level, string Category, string Message, string? Detail);
}
