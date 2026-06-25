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

    private sealed class FakeConnectionSamplingSettings : IConnectionSamplingSettings
    {
        public bool IsEnabled { get; init; }

        public int IntervalSeconds { get; init; } = 60;
    }

    private sealed class FakeConnectionSamplingSource : IConnectionSamplingSource
    {
        public Exception? Exception { get; set; }

        public IReadOnlyList<ActiveConnection> Connections { get; init; } = [];

        public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Connections);
        }
    }

    private sealed class FakeConnectionSamplingStorage : IConnectionSamplingStorage
    {
        public int InsertedCount { get; init; }

        public List<ConnectionSamplingLogEntry> Logs { get; } = [];

        public int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections)
        {
            return InsertedCount;
        }

        public void AppendLog(string level, string category, string message, string? detail)
        {
            Logs.Add(new ConnectionSamplingLogEntry(level, category, message, detail));
        }
    }

    private readonly record struct ConnectionSamplingLogEntry(string Level, string Category, string Message, string? Detail);
}
