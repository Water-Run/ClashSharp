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
