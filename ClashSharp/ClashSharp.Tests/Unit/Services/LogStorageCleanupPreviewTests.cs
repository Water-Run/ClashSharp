using System.Globalization;
using ClashSharp.Service;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class LogStorageServiceTests
{
    private static readonly string[] CleanupPreviewTables =
    [
        "Logs", "Connections", "TrafficSnapshots", "ProfileTrafficStats", "NodeTrafficStats",
        "NodeHealthStats", "RuleHitStats", "TrafficCounterState", "TrafficConnectionCounters",
    ];

    [Fact]
    public async Task PreviewCleanupBefore_MatchesAllDeletedTablesAndPreservesExactBoundary()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        DateTimeOffset cutoff = SeedCleanupPreviewHistory(storage, database.Path);
        string[] before = ReadCleanupPreviewRows(database.Path);

        long preview = storage.PreviewCleanupBefore(cutoff);

        Assert.Equal(5, preview);
        Assert.Equal(before, ReadCleanupPreviewRows(database.Path));
        storage.CleanupBefore(cutoff);
        string[] after = ReadCleanupPreviewRows(database.Path);
        Assert.Equal(preview, before.Length - after.Length);
        foreach (string table in new[] { "Logs", "Connections", "TrafficSnapshots", "NodeHealthStats", "RuleHitStats" })
        {
            string[] retained = after.Where(row => row.StartsWith(table + "|", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, retained.Length);
            Assert.DoesNotContain(retained, row => row.Contains("old", StringComparison.Ordinal));
            Assert.DoesNotContain(retained, row => row.Contains("1999999999", StringComparison.Ordinal));
            Assert.Single(retained, row => row.Contains("2000000000", StringComparison.Ordinal));
        }
        Assert.Equal(0, storage.PreviewCleanupBefore(cutoff));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(3L)]
    [InlineData(10L)]
    [InlineData(long.MaxValue)]
    public async Task PreviewCleanupToLogCount_MatchesDeletionIncludingTiedTimestamps(long keepCount)
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedCleanupPreviewHistory(storage, database.Path);
        using (SqliteConnection connection = new($"Data Source={database.Path}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE Logs SET CreatedAtUnixTime = 2000000000;";
            command.ExecuteNonQuery();
        }
        string[] before = ReadCleanupPreviewRows(database.Path);

        long preview = storage.PreviewCleanupToLogCount(keepCount);

        Assert.Equal(Math.Max(0, 3 - keepCount), preview);
        Assert.Equal(before, ReadCleanupPreviewRows(database.Path));
        storage.CleanupToLogCount(keepCount);
        string[] after = ReadCleanupPreviewRows(database.Path);
        Assert.Equal(preview, before.Length - after.Length);
        Assert.Equal(
            before.Where(row => !row.StartsWith("Logs|", StringComparison.Ordinal)),
            after.Where(row => !row.StartsWith("Logs|", StringComparison.Ordinal)));
        Assert.Equal(0, storage.PreviewCleanupToLogCount(keepCount));
        if (keepCount == 1)
        {
            Assert.Equal("new", Assert.Single(storage.GetLogs(10)).Message);
        }
    }

    [Fact]
    public async Task PreviewClearAll_IncludesStatisticsAndExcludesRetainedCounterBaselines()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedCleanupPreviewHistory(storage, database.Path);
        string[] before = ReadCleanupPreviewRows(database.Path);

        long preview = storage.PreviewClearAll();

        Assert.Equal(17, preview);
        Assert.Equal(before, ReadCleanupPreviewRows(database.Path));
        storage.ClearAll();
        string[] after = ReadCleanupPreviewRows(database.Path);
        Assert.Equal(preview, before.Length - after.Length);
        Assert.Equal(before.Where(row =>
            row.StartsWith("TrafficCounterState|", StringComparison.Ordinal) ||
            row.StartsWith("TrafficConnectionCounters|", StringComparison.Ordinal)), after);
        Assert.Equal(0, storage.PreviewClearAll());
    }

    [Fact]
    public async Task PreviewCleanupToLogCount_RejectsNegativeCountWithoutChangingStorage()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedCleanupPreviewHistory(storage, database.Path);
        string[] before = ReadCleanupPreviewRows(database.Path);

        Assert.Throws<ArgumentOutOfRangeException>(() => storage.PreviewCleanupToLogCount(-1));

        Assert.Equal(before, ReadCleanupPreviewRows(database.Path));
    }

    private static DateTimeOffset SeedCleanupPreviewHistory(LogStorageService storage, string path)
    {
        _ = storage.GetStorageSummary();
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Logs (CreatedAtUnixTime, Level, Source, Message) VALUES
                (1999999999, 'Info', 'Preview', 'old'), (2000000000, 'Info', 'Preview', 'boundary'), (2000000001, 'Info', 'Preview', 'new');
            INSERT INTO Connections (CreatedAtUnixTime, Host) VALUES
                (1999999999, 'old'), (2000000000, 'boundary'), (2000000001, 'new');
            INSERT INTO TrafficSnapshots (CreatedAtUnixTime, UploadBytes, DownloadBytes) VALUES
                (1999999999, 1, 2), (2000000000, 3, 4), (2000000001, 5, 6);
            INSERT INTO NodeHealthStats (NodeName, UpdatedAtUnixTime) VALUES
                ('old', 1999999999), ('boundary', 2000000000), ('new', 2000000001);
            INSERT INTO RuleHitStats (RuleName, UpdatedAtUnixTime) VALUES
                ('old', 1999999999), ('boundary', 2000000000), ('new', 2000000001);
            INSERT INTO ProfileTrafficStats (ProfileId, UpdatedAtUnixTime) VALUES ('profile-a', 1999999999);
            INSERT INTO NodeTrafficStats (NodeName, UpdatedAtUnixTime) VALUES ('node-a', 1999999999);
            INSERT INTO TrafficCounterState (Id, Epoch, UploadBytes, DownloadBytes) VALUES (1, 'epoch-a', 100, 200);
            INSERT INTO TrafficConnectionCounters (ConnectionId, StartedAtTicks, UploadBytes, DownloadBytes) VALUES ('connection-a', 123, 50, 60);
            """;
        command.ExecuteNonQuery();
        return DateTimeOffset.FromUnixTimeSeconds(2000000000);
    }

    private static string[] ReadCleanupPreviewRows(string path)
    {
        using SqliteConnection connection = new($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        List<string> rows = [];
        foreach (string table in CleanupPreviewTables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string[] values = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty)
                    .ToArray();
                rows.Add(table + "|" + string.Join('|', values));
            }
        }
        return rows.ToArray();
    }
}
