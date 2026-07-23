using System.Globalization;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies last-known-good trigger backup promotion and corruption recovery.</summary>
public sealed class TriggerBackupRecoveryTests
{
    [Fact]
    public async Task CreateBackupAsync_ProducesValidatedStandaloneSnapshotWithoutSidecars()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        await ReplaceRequiredAsync(repository, 0, [Definition("first", 1)]);

        TriggerPersistenceResult firstBackup =
            await repository.CreateBackupAsync(CancellationToken.None);
        await ReplaceRequiredAsync(repository, 1, [Definition("second", 1)]);
        TriggerPersistenceResult secondBackup =
            await repository.CreateBackupAsync(CancellationToken.None);

        Assert.True(firstBackup.IsSucceeded);
        Assert.True(secondBackup.IsSucceeded);
        Assert.Equal(2, await ReadGenerationAsync(directory.BackupPath));
        Assert.Equal("ok", await ReadScalarStringAsync(directory.BackupPath, "PRAGMA integrity_check;"));
        Assert.False(File.Exists(directory.BackupPath + "-wal"));
        Assert.False(File.Exists(directory.BackupPath + "-shm"));
        Assert.Empty(Directory.GetFiles(directory.RootPath, "Triggers.db.backup.tmp.*"));
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeBackup, 1)]
    [InlineData(TriggerPersistenceFaultPoint.AfterBackup, 1)]
    [InlineData(TriggerPersistenceFaultPoint.AfterBackupValidation, 1)]
    [InlineData(TriggerPersistenceFaultPoint.BeforeBackupPromotion, 1)]
    [InlineData(TriggerPersistenceFaultPoint.AfterBackupPromotion, 2)]
    public async Task CreateBackupAsync_FaultCutKeepsOldOrCompleteNewAuthority(
        TriggerPersistenceFaultPoint faultPoint,
        long expectedBackupGeneration)
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        await ReplaceRequiredAsync(repository, 0, [Definition("first", 1)]);
        Assert.True((await repository.CreateBackupAsync(CancellationToken.None)).IsSucceeded);
        await ReplaceRequiredAsync(repository, 1, [Definition("second", 1)]);
        SqliteTriggerRepository faultedRepository = directory.CreateRepository(
            new ThrowingFaultInjector(faultPoint));
        await OpenRequiredAsync(faultedRepository);

        TriggerPersistenceResult result =
            await faultedRepository.CreateBackupAsync(CancellationToken.None);

        Assert.Equal(TriggerPersistenceStatus.Unavailable, result.Status);
        Assert.Equal(expectedBackupGeneration, await ReadGenerationAsync(directory.BackupPath));
        Assert.Equal("ok", await ReadScalarStringAsync(directory.BackupPath, "PRAGMA integrity_check;"));
        Assert.Empty(Directory.GetFiles(directory.RootPath, "Triggers.db.backup.tmp.*"));
    }

    [Fact]
    public async Task OpenAsync_CorruptPrimaryRestoresValidatedBackupAndRecordsDiagnostic()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        await ReplaceRequiredAsync(repository, 0, [Definition("backed-up", 1)]);
        Assert.True((await repository.CreateBackupAsync(CancellationToken.None)).IsSucceeded);
        await ReplaceRequiredAsync(repository, 1, [Definition("newer", 1)]);
        directory.Corrupt(directory.DatabasePath);

        TriggerPersistenceResult<TriggerRepositorySnapshot> reopened =
            await directory.CreateRepository().OpenAsync(CancellationToken.None);

        Assert.True(reopened.IsSucceeded, reopened.Diagnostic?.Code);
        TriggerRepositorySnapshot snapshot = Assert.IsType<TriggerRepositorySnapshot>(reopened.Value);
        Assert.Equal(1, snapshot.DefinitionGeneration);
        Assert.Equal("backed-up", Assert.Single(snapshot.Tasks).Definition.Id);
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Code == "trigger.storage.recovered"
            && diagnostic.Severity == TriggerDiagnosticSeverity.Warning);
        Assert.Equal("ok", await ReadScalarStringAsync(directory.DatabasePath, "PRAGMA integrity_check;"));
        Assert.NotEmpty(Directory.GetFiles(directory.RootPath, "Triggers.db.corrupt.*"));
    }

    [Fact]
    public async Task OpenAsync_CorruptPrimaryAndBackupCreatesDiagnosedSafeEmptyDatabase()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        await ReplaceRequiredAsync(repository, 0, [Definition("task", 1)]);
        Assert.True((await repository.CreateBackupAsync(CancellationToken.None)).IsSucceeded);
        directory.Corrupt(directory.DatabasePath);
        directory.Corrupt(directory.BackupPath);

        TriggerPersistenceResult<TriggerRepositorySnapshot> reopened =
            await directory.CreateRepository().OpenAsync(CancellationToken.None);

        Assert.True(reopened.IsSucceeded, reopened.Diagnostic?.Code);
        TriggerRepositorySnapshot snapshot = Assert.IsType<TriggerRepositorySnapshot>(reopened.Value);
        Assert.Empty(snapshot.Tasks);
        Assert.Equal(0, snapshot.DefinitionGeneration);
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Code == "trigger.storage.safe_empty"
            && diagnostic.Severity == TriggerDiagnosticSeverity.Error);
        Assert.True(Directory.GetFiles(directory.RootPath, "*.corrupt.*").Length >= 2);
        Assert.Equal("ok", await ReadScalarStringAsync(directory.DatabasePath, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task OpenAsync_SemanticallyInvalidPrimaryRestoresValidatedBackup()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        await ReplaceRequiredAsync(repository, 0, [Definition("backed-up", 1)]);
        Assert.True((await repository.CreateBackupAsync(CancellationToken.None)).IsSucceeded);
        await using (SqliteConnection connection = await directory.OpenConnectionAsync())
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO trigger_diagnostics(code, severity, task_id, detail, occurred_at)
                VALUES ('trigger.invalid', 999, NULL, 'invalid', '2026-07-23T00:00:00.0000000+00:00');
                """);
        }

        TriggerPersistenceResult<TriggerRepositorySnapshot> reopened =
            await directory.CreateRepository().OpenAsync(CancellationToken.None);

        Assert.True(reopened.IsSucceeded, reopened.Diagnostic?.Code);
        TriggerRepositorySnapshot snapshot = Assert.IsType<TriggerRepositorySnapshot>(reopened.Value);
        Assert.Equal("backed-up", Assert.Single(snapshot.Tasks).Definition.Id);
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.Code == "trigger.invalid");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "trigger.storage.recovered");
    }

    [Fact]
    public async Task OpenAsync_InvalidDurableOutboxRestoresValidatedBackupBeforeDispatch()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        TriggerTaskDefinition definition = Definition("task", 1);
        await ReplaceRequiredAsync(repository, 0, [definition]);
        TriggerPersistenceResult<TriggerRepositorySnapshot> read =
            await repository.ReadSnapshotAsync(CancellationToken.None);
        TriggerTaskState state = Assert.IsType<TriggerRepositorySnapshot>(read.Value).Tasks[0].State;
        TriggerPersistenceResult<TriggerExecution> committed =
            await repository.TryCommitExecutionAsync(
                new TriggerExecutionCommitRequest(
                    Guid.NewGuid(),
                    definition,
                    state.Version,
                    state,
                    DateTimeOffset.UnixEpoch,
                    Guid.NewGuid()),
                CancellationToken.None);
        Assert.True(committed.IsSucceeded);
        Assert.True((await repository.CreateBackupAsync(CancellationToken.None)).IsSucceeded);
        await using (SqliteConnection connection = await directory.OpenConnectionAsync())
        {
            await ExecuteNonQueryAsync(
                connection,
                "PRAGMA ignore_check_constraints = ON; UPDATE trigger_outbox SET action_kind = 999;");
        }

        SqliteTriggerRepository reopenedRepository = directory.CreateRepository();
        TriggerPersistenceResult<TriggerRepositorySnapshot> reopened =
            await reopenedRepository.OpenAsync(CancellationToken.None);
        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> recoverable =
            await reopenedRepository.ReadRecoverableActionsAsync(CancellationToken.None);

        Assert.True(reopened.IsSucceeded, reopened.Diagnostic?.Code);
        Assert.Contains(
            Assert.IsType<TriggerRepositorySnapshot>(reopened.Value).Diagnostics,
            diagnostic => diagnostic.Code == "trigger.storage.recovered");
        Assert.True(recoverable.IsSucceeded, recoverable.Diagnostic?.Code);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TriggerOutboxAction>>(recoverable.Value));
    }

    [Fact]
    public async Task ReplaceDefinitionsAsync_BusyWriterReturnsTypedUnavailable()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository(busyTimeout: TimeSpan.FromMilliseconds(100));
        await OpenRequiredAsync(repository);
        await using SqliteConnection blocker = await directory.OpenConnectionAsync();
        await ExecuteNonQueryAsync(blocker, "BEGIN IMMEDIATE;");

        try
        {
            TriggerPersistenceResult result = await repository.ReplaceDefinitionsAsync(
                new TriggerDefinitionWriteRequest(0, [Definition("task", 1)]),
                CancellationToken.None);

            Assert.Equal(TriggerPersistenceStatus.Unavailable, result.Status);
            Assert.Equal("trigger.storage.write_failed", result.Diagnostic?.Code);
        }
        finally
        {
            await ExecuteNonQueryAsync(blocker, "ROLLBACK;");
        }
    }

    [Fact]
    public async Task OpenAsync_PathBlockedByFileReturnsTypedUnavailable()
    {
        using TemporaryTriggerDirectory directory = new();
        string blockerPath = Path.Combine(directory.RootPath, "not-a-directory");
        await File.WriteAllTextAsync(blockerPath, "blocker", CancellationToken.None);
        SqliteTriggerRepository repository = new(Path.Combine(blockerPath, "Triggers.db"));

        TriggerPersistenceResult<TriggerRepositorySnapshot> result =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(TriggerPersistenceStatus.Unavailable, result.Status);
        Assert.Equal("trigger.storage.open_failed", result.Diagnostic?.Code);
    }

    private static TriggerTaskDefinition Definition(string id, long revision)
    {
        return new TriggerTaskDefinition(
            id,
            revision,
            id,
            true,
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters(id)),
            ]);
    }

    private static async Task<TriggerRepositorySnapshot> OpenRequiredAsync(
        SqliteTriggerRepository repository)
    {
        TriggerPersistenceResult<TriggerRepositorySnapshot> result =
            await repository.OpenAsync(CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsType<TriggerRepositorySnapshot>(result.Value);
    }

    private static async Task ReplaceRequiredAsync(
        SqliteTriggerRepository repository,
        long expectedGeneration,
        IEnumerable<TriggerTaskDefinition> definitions)
    {
        TriggerPersistenceResult result = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(expectedGeneration, definitions),
            CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
    }

    private static async Task<long> ReadGenerationAsync(string databasePath)
    {
        string text = await ReadScalarStringAsync(
            databasePath,
            "SELECT value FROM trigger_metadata WHERE key = 'definition_generation';");
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadScalarStringAsync(
        string databasePath,
        string commandText)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return Assert.IsType<string>(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed class TemporaryTriggerDirectory : IDisposable
    {
        public TemporaryTriggerDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "ClashSharp",
                "TriggerBackupTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string DatabasePath => Path.Combine(RootPath, "Triggers.db");

        public string BackupPath => DatabasePath + ".backup";

        public SqliteTriggerRepository CreateRepository(
            ITriggerPersistenceFaultInjector? faultInjector = null,
            TimeSpan? busyTimeout = null)
        {
            return new SqliteTriggerRepository(
                DatabasePath,
                faultInjector,
                busyTimeout ?? TimeSpan.FromSeconds(1));
        }

        public async Task<SqliteConnection> OpenConnectionAsync()
        {
            SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(CancellationToken.None);
            return connection;
        }

        public void Corrupt(string path)
        {
            SqliteConnection.ClearAllPools();
            File.WriteAllText(path, "not a sqlite database");
            DeleteIfExists(path + "-wal");
            DeleteIfExists(path + "-shm");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class ThrowingFaultInjector(TriggerPersistenceFaultPoint faultPoint)
        : ITriggerPersistenceFaultInjector
    {
        public Task InjectAsync(
            TriggerPersistenceFaultPoint observedPoint,
            CancellationToken cancellationToken)
        {
            if (observedPoint == faultPoint)
            {
                throw new IOException("Injected trigger backup failure.");
            }

            return Task.CompletedTask;
        }
    }
}
