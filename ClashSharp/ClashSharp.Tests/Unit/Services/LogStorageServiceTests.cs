/*
 * Log Storage Service Tests
 * Verifies SQLite traffic aggregation through injected active profile dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/LogStorageServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for SQLite log storage behavior.</summary>
public sealed class LogStorageServiceTests
{
    /// <summary>Verifies connection snapshots aggregate profile traffic using the injected active profile id.</summary>
    [Fact]
    public void AppendConnectionSnapshot_UsesInjectedActiveProfileId()
    {
        using TempDatabase tempDatabase = new();
        LogStorageService service = new(tempDatabase.Path, () => "profile-a");
        ActiveConnection connection = new(
            "1",
            "curl",
            "example.com",
            "DOMAIN-SUFFIX",
            "example.com",
            "Proxy A",
            100,
            200,
            DateTimeOffset.UtcNow);

        int inserted = service.AppendConnectionSnapshot([connection]);

        TrafficStatisticRow row = Assert.Single(service.GetProfileTrafficRows(1));
        Assert.Equal(1, inserted);
        Assert.Equal("profile-a", row.Label);
        Assert.Equal(100, row.UploadBytes);
        Assert.Equal(200, row.DownloadBytes);
        Assert.Equal(1, row.SampleCount);
    }

    /// <summary>Verifies node health latency can be read by node name for tray status display.</summary>
    [Fact]
    public void GetNodeLatencyMilliseconds_ReturnsStoredLatency()
    {
        using TempDatabase tempDatabase = new();
        LogStorageService service = new(tempDatabase.Path, () => "profile-a");
        service.UpsertNodeHealth("Proxy A", "US", 86);

        int? latency = service.GetNodeLatencyMilliseconds("Proxy A");

        Assert.Equal(86, latency);
    }

    /// <summary>Verifies application logs can be searched and filtered by visible log fields.</summary>
    [Fact]
    public void GetLogs_FiltersBySourceLevelAndSearchText()
    {
        using TempDatabase tempDatabase = new();
        LogStorageService service = new(tempDatabase.Path, () => "profile-a");
        service.AppendLog("Info", "Notification", "Notification sent", "Title: Trigger\nMessage: Threshold reached");
        service.AppendLog("Warning", "Trigger", "Trigger fired", "Task: Daily guard");
        service.AppendLog("Info", "Settings", "Setting changed: MixedPort", "Previous: 10000\nNew: 12000");

        IReadOnlyList<LogRecord> notificationLogs = service.GetLogs(20, "Notification", "Info", "Threshold");
        IReadOnlyList<LogRecord> settingLogs = service.GetLogs(20, null, null, "MixedPort");
        IReadOnlyList<string> sources = service.GetLogSources();

        LogRecord notification = Assert.Single(notificationLogs);
        Assert.Equal("Notification", notification.Source);
        Assert.Equal("Info", notification.Level);
        Assert.Contains("Threshold reached", notification.Detail, StringComparison.Ordinal);
        LogRecord setting = Assert.Single(settingLogs);
        Assert.Equal("Settings", setting.Source);
        Assert.Contains("Notification", sources);
        Assert.Contains("Settings", sources);
        Assert.Contains("Trigger", sources);
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clashsharp-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "logs.sqlite3");
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
