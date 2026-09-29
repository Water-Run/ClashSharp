using System.Globalization;
using ClashSharp.Service;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class LogStorageServiceTests
{
    [Fact]
    public async Task CleanupToSize_RetainsRecentLogsAndNewerConnectionHistory()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedSizeCleanupHistory(storage, database.Path);
        string[] before = ReadCleanupPreviewRows(database.Path);

        storage.CleanupToSize(1024 * 1024);

        Assert.InRange(storage.GetStorageSummary().DatabaseSizeBytes, 1, 1024 * 1024);
        int[] retained = storage.GetLogs(2000, "SizeCleanup")
            .Select(row => int.Parse(row.Message, CultureInfo.InvariantCulture)).ToArray();
        Assert.InRange(retained.Length, 500, 1499);
        Assert.Equal(Enumerable.Range(1500 - retained.Length, retained.Length).Reverse(), retained);
        Assert.Equal(before.Where(IsSizeRetainedTable), ReadCleanupPreviewRows(database.Path).Where(IsSizeRetainedTable));
    }

    [Fact]
    public async Task CleanupToSize_SmallExcessDoesNotDeleteAnEntireBatchOfRecentHistory()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedSizeCleanupHistory(storage, database.Path);
        using (SqliteConnection connection = new($"Data Source={database.Path}"))
        {
            connection.Open();
            LogStorageMaintenance.Vacuum(connection);
        }
        long target = storage.GetStorageSummary().DatabaseSizeBytes - 8192;

        storage.CleanupToSize(target);

        Assert.InRange(storage.GetStorageSummary().DatabaseSizeBytes, 1, target);
        Assert.InRange(storage.GetLogs(2000, "SizeCleanup").Count, 1400, 1499);
    }

    [Fact]
    public async Task CleanupToSize_ReclaimsFreePagesWithoutDeletingAnyRemainingRows()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedSizeCleanupHistory(storage, database.Path);
        using (SqliteConnection connection = new($"Data Source={database.Path}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Logs WHERE Source = 'SizeCleanup' AND CAST(Message AS INTEGER) < 1490;";
            command.ExecuteNonQuery();
        }
        string[] before = ReadCleanupPreviewRows(database.Path);
        Assert.True(storage.GetStorageSummary().DatabaseSizeBytes > 1024 * 1024);

        storage.CleanupToSize(1024 * 1024);

        Assert.Equal(before, ReadCleanupPreviewRows(database.Path));
        Assert.InRange(storage.GetStorageSummary().DatabaseSizeBytes, 1, 1024 * 1024);
    }

    [Fact]
    public async Task CleanupToSize_UnattainableTargetPreservesAggregatesAndLiveCounterBaselines()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedCleanupPreviewHistory(storage, database.Path);
        string[] before = ReadCleanupPreviewRows(database.Path);

        storage.CleanupToSize(0);

        string[] after = ReadCleanupPreviewRows(database.Path);
        Assert.Equal(before.Where(row => !IsSizeHistoryTable(row)), after);
        Assert.True(storage.GetStorageSummary().DatabaseSizeBytes > 0);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task CleanupToSize_InvalidOrAlreadySatisfiedTargetPreservesRows(long target)
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedCleanupPreviewHistory(storage, database.Path);
        string[] before = ReadCleanupPreviewRows(database.Path);

        if (target < 0)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => storage.CleanupToSize(target));
        }
        else
        {
            storage.CleanupToSize(target);
        }

        Assert.Equal(before, ReadCleanupPreviewRows(database.Path));
    }

    [Fact]
    public async Task SizeCleanupBatch_UsesGlobalChronologyAndStableTimestampTies()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedCleanupPreviewHistory(storage, database.Path);
        using SqliteConnection connection = new($"Data Source={database.Path}");
        connection.Open();

        Assert.Equal(4, LogStorageMaintenance.DeleteOldestHistoryBatch(connection, 4));

        Assert.Equal("new", Assert.Single(storage.GetLogs(10)).Message);
        string[] after = ReadCleanupPreviewRows(database.Path);
        string[] retainedHistory = after.Where(IsSizeHistoryTable).ToArray();
        Assert.Equal(5, retainedHistory.Length);
        Assert.DoesNotContain(retainedHistory, row => row.Contains("1999999999", StringComparison.Ordinal));
        Assert.Equal(2, retainedHistory.Count(row => row.Contains("2000000000", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SizeCleanupBatch_FailedDeletionRollsBackAllThreeTables()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedCleanupPreviewHistory(storage, database.Path);
        string[] before = ReadCleanupPreviewRows(database.Path);
        using SqliteConnection connection = new($"Data Source={database.Path}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectCleanup BEFORE DELETE ON Connections BEGIN SELECT RAISE(FAIL, 'injected'); END;";
        command.ExecuteNonQuery();

        Assert.Throws<SqliteException>(() => LogStorageMaintenance.DeleteOldestHistoryBatch(connection, 4));

        Assert.Equal(before, ReadCleanupPreviewRows(database.Path));
    }

    [Fact]
    public async Task SizeCleanupCompaction_BusyReaderReturnsFalseAndPreservesRows()
    {
        using TempDatabase database = new();
        await using LogStorageService storage = new(database.Path, static () => "profile-a");
        SeedCleanupPreviewHistory(storage, database.Path);
        using SqliteConnection readerConnection = new($"Data Source={database.Path};Default Timeout=1");
        readerConnection.Open();
        using SqliteCommand read = readerConnection.CreateCommand();
        read.CommandText = "SELECT * FROM Logs;";
        using SqliteDataReader reader = read.ExecuteReader();
        Assert.True(reader.Read());
        storage.AppendLog("Info", "SizeCleanup", "new WAL record", null);
        using SqliteConnection maintenance = new($"Data Source={database.Path};Default Timeout=1");
        maintenance.Open();

        Assert.False(LogStorageMaintenance.TryVacuumForSizeCleanup(maintenance));

        Assert.Equal(4, storage.GetLogs(10).Count);
    }

    private static bool IsSizeHistoryTable(string row) => row.StartsWith("Logs|", StringComparison.Ordinal)
        || row.StartsWith("Connections|", StringComparison.Ordinal)
        || row.StartsWith("TrafficSnapshots|", StringComparison.Ordinal);

    private static bool IsSizeRetainedTable(string row) => !row.StartsWith("Logs|", StringComparison.Ordinal);

    private static void SeedSizeCleanupHistory(LogStorageService storage, string path)
    {
        SeedCleanupPreviewHistory(storage, path);
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Logs (CreatedAtUnixTime, Level, Source, Message, Detail) VALUES ($time, 'Info', 'SizeCleanup', $message, $detail);";
        SqliteParameter time = command.Parameters.Add("$time", SqliteType.Integer);
        SqliteParameter message = command.Parameters.Add("$message", SqliteType.Text);
        command.Parameters.AddWithValue("$detail", new string('x', 1000));
        for (int index = 0; index < 1500; index++)
        {
            time.Value = 1900000000 + index;
            message.Value = index.ToString(CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
