using System.Diagnostics;
using System.Globalization;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies durable trigger authority across real process termination cut points.</summary>
public sealed class TriggerPersistenceCrashTests
{
    private const int CrashExitCode = 86;

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeMigrationCommit, 0, TriggerMigrationStatus.Migrated)]
    [InlineData(TriggerPersistenceFaultPoint.AfterMigrationCommit, 1, TriggerMigrationStatus.Finalized)]
    public async Task MigrationCrash_RestartRecoversExactlyOneCompleteImport(
        TriggerPersistenceFaultPoint faultPoint,
        long generationAfterCrash,
        TriggerMigrationStatus recoveryStatus)
    {
        using CrashDirectory directory = new();
        await File.WriteAllTextAsync(
            directory.LegacyPath,
            """
            [{
              "Id": "crash-safe", "Name": "Crash safe", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "complete" }]
            }]
            """,
            CancellationToken.None);

        int exitCode = await RunProbeAsync("migrate", directory.RootPath, faultPoint);

        Assert.Equal(CrashExitCode, exitCode);
        Assert.Equal(generationAfterCrash, await ReadGenerationAsync(directory.DatabasePath));
        Assert.True(File.Exists(directory.LegacyPath));
        Assert.True(File.Exists(directory.IntentPath));

        SqliteTriggerRepository repository = directory.CreateRepository();
        TriggerMigrationResult recovered = await new TriggerMigrationCoordinator(
            repository,
            directory.LegacyPath).MigrateAsync(CancellationToken.None);
        TriggerPersistenceResult<TriggerRepositorySnapshot> read =
            await repository.ReadSnapshotAsync(CancellationToken.None);

        Assert.Equal(recoveryStatus, recovered.Status);
        Assert.True(read.IsSucceeded, read.Diagnostic?.Code);
        TriggerRepositorySnapshot snapshot = Assert.IsType<TriggerRepositorySnapshot>(read.Value);
        Assert.Equal(1, snapshot.DefinitionGeneration);
        Assert.Equal("crash-safe", Assert.Single(snapshot.Tasks).Definition.Id);
        Assert.False(File.Exists(directory.LegacyPath));
        Assert.False(File.Exists(directory.IntentPath));
        Assert.Single(Directory.GetFiles(directory.RootPath, "Triggers.json.migration-backup.*"));
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeBackupPromotion, 1)]
    [InlineData(TriggerPersistenceFaultPoint.AfterBackupPromotion, 2)]
    public async Task BackupCrash_RestartKeepsACompleteAuthorityAndCleansOrphanCandidates(
        TriggerPersistenceFaultPoint faultPoint,
        long generationAfterCrash)
    {
        using CrashDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        await ReplaceRequiredAsync(repository, 0, Definition("first"));
        Assert.True((await repository.CreateBackupAsync(CancellationToken.None)).IsSucceeded);
        await ReplaceRequiredAsync(repository, 1, Definition("second"));

        int exitCode = await RunProbeAsync("backup", directory.RootPath, faultPoint);

        Assert.Equal(CrashExitCode, exitCode);
        Assert.Equal(2, await ReadGenerationAsync(directory.DatabasePath));
        Assert.Equal(generationAfterCrash, await ReadGenerationAsync(directory.BackupPath));

        SqliteTriggerRepository recoveredRepository = directory.CreateRepository();
        await OpenRequiredAsync(recoveredRepository);
        TriggerPersistenceResult recoveredBackup =
            await recoveredRepository.CreateBackupAsync(CancellationToken.None);

        Assert.True(recoveredBackup.IsSucceeded, recoveredBackup.Diagnostic?.Code);
        Assert.Equal(2, await ReadGenerationAsync(directory.BackupPath));
        Assert.Empty(Directory.GetFiles(directory.RootPath, "Triggers.db.backup.tmp.*"));
        Assert.False(File.Exists(directory.BackupPath + "-wal"));
        Assert.False(File.Exists(directory.BackupPath + "-shm"));
    }

    private static TriggerTaskDefinition Definition(string id)
    {
        return new TriggerTaskDefinition(
            id,
            1,
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

    private static async Task OpenRequiredAsync(SqliteTriggerRepository repository)
    {
        TriggerPersistenceResult<TriggerRepositorySnapshot> opened =
            await repository.OpenAsync(CancellationToken.None);
        Assert.True(opened.IsSucceeded, opened.Diagnostic?.Code);
    }

    private static async Task ReplaceRequiredAsync(
        SqliteTriggerRepository repository,
        long expectedGeneration,
        TriggerTaskDefinition definition)
    {
        TriggerPersistenceResult replaced = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(expectedGeneration, [definition]),
            CancellationToken.None);
        Assert.True(replaced.IsSucceeded, replaced.Diagnostic?.Code);
    }

    private static async Task<long> ReadGenerationAsync(string databasePath)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM trigger_metadata WHERE key = 'definition_generation';";
        string value = Assert.IsType<string>(
            await command.ExecuteScalarAsync(CancellationToken.None));
        return long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> RunProbeAsync(
        string operation,
        string rootPath,
        TriggerPersistenceFaultPoint faultPoint)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(FindProbePath());
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(rootPath);
        startInfo.ArgumentList.Add(faultPoint.ToString());
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The trigger persistence probe could not start.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The trigger persistence probe did not terminate.");
        }

        string output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        string error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        Assert.True(
            process.ExitCode == CrashExitCode,
            $"Probe exit {process.ExitCode}. stdout: {output} stderr: {error}");
        return process.ExitCode;
    }

    private static string FindProbePath()
    {
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        string path = Path.Combine(
            FindRepositoryRoot(),
            "ClashSharp",
            "ClashSharp.TriggerProbe",
            "bin",
            "x64",
            configuration,
            "net10.0-windows10.0.22000.0",
            "ClashSharp.TriggerProbe.dll");
        Assert.True(File.Exists(path), $"Trigger persistence probe was not built: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClashSharp", "ClashSharp.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output.");
    }

    private sealed class CrashDirectory : IDisposable
    {
        public CrashDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "ClashSharp",
                "TriggerCrashTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string DatabasePath => Path.Combine(RootPath, "Triggers.db");

        public string BackupPath => DatabasePath + ".backup";

        public string LegacyPath => Path.Combine(RootPath, "Triggers.json");

        public string IntentPath => LegacyPath + ".migration-intent";

        public SqliteTriggerRepository CreateRepository()
        {
            return new SqliteTriggerRepository(
                DatabasePath,
                busyTimeout: TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
