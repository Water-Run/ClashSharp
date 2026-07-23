using Microsoft.Data.Sqlite;

namespace ClashSharp.Infrastructure.Triggers;

internal sealed class TriggerBackupManager
{
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly int _busyTimeoutMilliseconds;
    private readonly ITriggerPersistenceFaultInjector _faultInjector;

    public TriggerBackupManager(
        string databasePath,
        string backupPath,
        int busyTimeoutMilliseconds,
        ITriggerPersistenceFaultInjector faultInjector)
    {
        _databasePath = databasePath;
        _backupPath = backupPath;
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        _faultInjector = faultInjector;
    }

    public async Task CreateAsync(CancellationToken cancellationToken)
    {
        using FileStream operationLease = AcquireOperationLease();
        DeleteOrphanedTemporaryFiles(_backupPath + ".tmp.");
        await _faultInjector.InjectAsync(
            TriggerPersistenceFaultPoint.BeforeBackup,
            cancellationToken).ConfigureAwait(false);
        string temporaryPath = _backupPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await Task.Run(
                () => BackupDatabase(_databasePath, temporaryPath),
                cancellationToken).ConfigureAwait(false);
            await _faultInjector.InjectAsync(
                TriggerPersistenceFaultPoint.AfterBackup,
                cancellationToken).ConfigureAwait(false);
            await ValidateDatabaseAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            FlushFile(temporaryPath);
            await _faultInjector.InjectAsync(
                TriggerPersistenceFaultPoint.AfterBackupValidation,
                cancellationToken).ConfigureAwait(false);
            await _faultInjector.InjectAsync(
                TriggerPersistenceFaultPoint.BeforeBackupPromotion,
                cancellationToken).ConfigureAwait(false);
            Promote(temporaryPath, _backupPath);
            await _faultInjector.InjectAsync(
                TriggerPersistenceFaultPoint.AfterBackupPromotion,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteDatabaseFiles(temporaryPath);
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        using FileStream operationLease = AcquireOperationLease();
        DeleteOrphanedTemporaryFiles(_backupPath + ".tmp.");
        DeleteOrphanedTemporaryFiles(_databasePath + ".restore.tmp.");
        string temporaryPath = _databasePath + ".restore.tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await Task.Run(
                () => BackupDatabase(_backupPath, temporaryPath),
                cancellationToken).ConfigureAwait(false);
            await ValidateDatabaseAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            FlushFile(temporaryPath);
            QuarantineDatabaseFiles(_databasePath);
            Promote(temporaryPath, _databasePath);
        }
        finally
        {
            DeleteDatabaseFiles(temporaryPath);
        }
    }

    public static void QuarantineDatabaseFiles(string path)
    {
        QuarantineIfExists(path);
        QuarantineIfExists(path + "-wal");
        QuarantineIfExists(path + "-shm");
    }

    private static void BackupDatabase(string sourcePath, string destinationPath)
    {
        using SqliteConnection source = new(CreateConnectionString(
            sourcePath,
            SqliteOpenMode.ReadOnly));
        using SqliteConnection destination = new(CreateConnectionString(
            destinationPath,
            SqliteOpenMode.ReadWriteCreate));
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        using SqliteCommand command = destination.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode = DELETE;";
        command.ExecuteNonQuery();
    }

    private async Task ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(CreateConnectionString(
            databasePath,
            SqliteOpenMode.ReadWrite));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand timeout = connection.CreateCommand();
        timeout.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds};";
        await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await TriggerDatabaseSchema.PrepareExistingAsync(
            connection,
            enableWal: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateConnectionString(string path, SqliteOpenMode mode)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
    }

    private static void FlushFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void Promote(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        DeleteIfExists(path);
        DeleteIfExists(path + "-wal");
        DeleteIfExists(path + "-shm");
    }

    private FileStream AcquireOperationLease()
    {
        return new FileStream(
            _backupPath + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
    }

    private static void DeleteOrphanedTemporaryFiles(string pathPrefix)
    {
        string directory = Path.GetDirectoryName(pathPrefix)
            ?? throw new InvalidDataException("Trigger backup directory is missing.");
        string pattern = Path.GetFileName(pathPrefix) + "*";
        foreach (string path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void QuarantineIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string quarantinePath = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{path}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
        File.Move(path, quarantinePath);
    }
}
