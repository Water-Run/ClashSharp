using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Services;

public sealed class AgentLogStorageTests
{
    [Fact]
    public async Task SessionDiagnostics_ExcludeLegacyReopenedAndConcurrentWritersAtTheSameTimestamp()
    {
        using Database database = new();
        await using (LogStorageService first = new(database.Path, () => "fixture"))
        {
            first.AppendLog("Info", "Fixture", "first session", null);
        }
        await using LogStorageService current = new(database.Path, () => "fixture");
        Assert.Empty(current.ReadSessionDiagnostics(100, CancellationToken.None));
        current.AppendLog("Warning", "Fixture", "current session", null);
        await using LogStorageService concurrent = new(database.Path, () => "fixture");
        concurrent.AppendLog("Info", "Fixture", "other writer", null);
        InsertLegacy(database.Path, "legacy session", "legacy detail");
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database.Path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE Logs SET CreatedAtUnixTime = 1;";
            command.ExecuteNonQuery();
        }

        StoredLogRecord row = Assert.Single(current.ReadSessionDiagnostics(100, CancellationToken.None));

        Assert.Equal("current session", row.Record.Message);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), row.Record.CreatedAt);
        Assert.Equal(4, current.ReadLogHistorySummaries(100, null, null, null, CancellationToken.None).Count);
    }

    [Fact]
    public async Task HistoryKeyset_NewEntriesDoNotShiftOlderPagesAndFiltersAreLiteral()
    {
        using Database database = new();
        await using LogStorageService storage = new(database.Path, () => "fixture");
        for (int index = 1; index <= 3; index++) { storage.AppendLog("Info", "Fixture", "entry " + index, "private-body"); }
        storage.AppendLog("Warning", "Other", "other source", null);
        var first = storage.ReadLogHistorySummaries(2, null, "Fixture", "Info", CancellationToken.None);
        Assert.Equal(2, first.Count);
        Assert.All(first, row => { Assert.Empty(row.Record.Message); Assert.Empty(row.Record.Detail); });
        storage.AppendLog("Info", "Fixture", "new entry", null);

        var second = storage.ReadLogHistorySummaries(2, first[^1].Id, "Fixture", "Info", CancellationToken.None);

        StoredLogRecord older = Assert.Single(second);
        Assert.True(older.Id < first[^1].Id);
        Assert.Equal("entry 1", storage.ReadLogHistoryEntry(older.Id, CancellationToken.None)!.Value.Record.Message);
        Assert.Empty(storage.ReadLogHistorySummaries(2, null, "Fixture' OR 1=1 --", null, CancellationToken.None));
    }

    [Fact]
    public async Task LegacyContent_IsRedactedAgainAndOversizedTextIsBoundedBeforeReturn()
    {
        using Database database = new();
        await using LogStorageService storage = new(database.Path, () => "fixture");
        _ = storage.GetStorageSummary();
        InsertLegacy(database.Path, "Authorization: Bearer private-fixture-secret\nvalidation_failed",
            @"https://user:private-password@example.test/private C:\Users\Private Name\config.yaml");
        StoredLogRecord summary = Assert.Single(storage.ReadLogHistorySummaries(100, null, "Legacy", null, CancellationToken.None));
        StoredLogRecord detail = storage.ReadLogHistoryEntry(summary.Id, CancellationToken.None)!.Value;
        Assert.Empty(summary.Record.Message);
        Assert.DoesNotContain("private-fixture-secret", detail.Record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", detail.Record.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", detail.Record.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Name", detail.Record.Detail, StringComparison.Ordinal);
        Assert.Contains("validation_failed", detail.Record.Message, StringComparison.Ordinal);
        Assert.Empty(storage.ReadSessionDiagnostics(100, CancellationToken.None));

        InsertLegacy(database.Path, new string('\uFFFF', 100_000), "bounded detail");
        StoredLogRecord large = storage.ReadLogHistoryEntry(storage.ReadLogHistorySummaries(1, null, "Legacy", null, CancellationToken.None)[0].Id, CancellationToken.None)!.Value;
        Assert.True(large.IsTruncated);
        Assert.Equal(4096, large.Record.Message.Length);
    }

    [Fact]
    public async Task RemovedHistoryId_DoesNotResolveToAReplacementAfterCleanup()
    {
        using Database database = new();
        await using LogStorageService storage = new(database.Path, () => "fixture");
        storage.AppendLog("Info", "Fixture", "original", null);
        long original = Assert.Single(storage.ReadLogHistorySummaries(1, null, null, null, CancellationToken.None)).Id;
        _ = storage.CleanupLogs(source: "Fixture");
        storage.AppendLog("Info", "Fixture", "replacement", null);

        Assert.Null(storage.ReadLogHistoryEntry(original, CancellationToken.None));
        Assert.True(Assert.Single(storage.ReadLogHistorySummaries(1, null, null, null, CancellationToken.None)).Id > original);
    }

    [Fact]
    public async Task CancellationAndDisposal_RefuseReadsWithoutCreatingData()
    {
        using Database database = new();
        LogStorageService storage = new(database.Path, () => "fixture");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => storage.ReadSessionDiagnostics(1, cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ReadLogHistorySummaries(102, null, null, null, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ReadLogHistoryEntry(0, CancellationToken.None));
        Assert.False(File.Exists(database.Path));
        await storage.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => storage.ReadSessionDiagnostics(1, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => storage.ReadLogHistorySummaries(1, null, null, null, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => storage.ReadLogHistoryEntry(1, CancellationToken.None));
    }

    private static void InsertLegacy(string path, string message, string detail)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Logs (CreatedAtUnixTime, Level, Source, Message, Detail) VALUES (1, 'Info', 'Legacy', $message, $detail);";
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$detail", detail);
        command.ExecuteNonQuery();
    }

    private sealed class Database : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClashSharp-AgentLogs", Guid.NewGuid().ToString("N"));
        public Database() { Directory.CreateDirectory(_directory); }
        public string Path => System.IO.Path.Combine(_directory, "logs.sqlite3");
        public void Dispose() { Directory.Delete(_directory, recursive: true); }
    }
}
