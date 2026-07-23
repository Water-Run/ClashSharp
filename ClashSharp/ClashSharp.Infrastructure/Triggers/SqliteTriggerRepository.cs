using System.Globalization;
using ClashSharp.ApplicationModel.Triggers;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Infrastructure.Triggers;

/// <summary>Transactional SQLite WAL repository for trigger definitions and durable execution state.</summary>
/// <remarks>
/// Each operation owns a short-lived connection. Writes are serialized per repository instance while
/// WAL readers remain independent. Expected storage failures are returned as typed diagnostics.
/// </remarks>
public sealed partial class SqliteTriggerRepository : ITriggerRepository
{
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly int _busyTimeoutMilliseconds;
    private readonly ITriggerPersistenceFaultInjector _faultInjector;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _isOpen;

    internal string DatabasePath => _databasePath;

    /// <summary>Initializes a trigger repository rooted at one database path.</summary>
    /// <param name="databasePath">Absolute or relative path to <c>Triggers.db</c>.</param>
    /// <param name="faultInjector">Optional deterministic persistence fault injector.</param>
    /// <param name="busyTimeout">Maximum SQLite lock wait before returning unavailable.</param>
    public SqliteTriggerRepository(
        string databasePath,
        ITriggerPersistenceFaultInjector? faultInjector = null,
        TimeSpan? busyTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        TimeSpan effectiveTimeout = busyTimeout ?? TimeSpan.FromSeconds(5);
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(busyTimeout));
        }

        _databasePath = Path.GetFullPath(databasePath);
        _backupPath = _databasePath + ".backup";
        _busyTimeoutMilliseconds = checked((int)Math.Ceiling(effectiveTimeout.TotalMilliseconds));
        _faultInjector = faultInjector ?? NullTriggerPersistenceFaultInjector.Instance;
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult<TriggerRepositorySnapshot>> OpenAsync(
        CancellationToken cancellationToken)
    {
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(_databasePath)
                ?? throw new InvalidDataException("Trigger database directory is missing.");
            Directory.CreateDirectory(directory);
            TriggerRepositorySnapshot snapshot;
            try
            {
                snapshot = await OpenPrimaryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableCorruption(exception))
            {
                snapshot = await RecoverCorruptStorageAsync(cancellationToken).ConfigureAwait(false);
            }

            _isOpen = true;
            return TriggerPersistenceResult.Succeeded(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            _isOpen = false;
            return TriggerPersistenceResult.Unavailable<TriggerRepositorySnapshot>(
                CreateDiagnostic("trigger.storage.open_failed", "open", exception));
        }
        finally
        {
            _openGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult<TriggerRepositorySnapshot>> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid<TriggerRepositorySnapshot>(CreateNotOpenDiagnostic());
        }

        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(
                SqliteOpenMode.ReadWrite,
                cancellationToken).ConfigureAwait(false);
            TriggerRepositorySnapshot snapshot = await ReadSnapshotCoreAsync(
                connection,
                null,
                cancellationToken).ConfigureAwait(false);
            return TriggerPersistenceResult.Succeeded(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable<TriggerRepositorySnapshot>(
                CreateDiagnostic("trigger.storage.read_failed", "read_snapshot", exception));
        }
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult> ReplaceDefinitionsAsync(
        TriggerDefinitionWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid(CreateNotOpenDiagnostic());
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReplaceDefinitionsCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable(
                CreateDiagnostic("trigger.storage.write_failed", "replace_definitions", exception));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult> TryImportMigrationAsync(
        TriggerMigrationImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid(CreateNotOpenDiagnostic());
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryImportMigrationCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable(
                CreateDiagnostic("trigger.storage.write_failed", "import_migration", exception));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async Task<string?> ReadLegacyMigrationSourceHashAsync(
        CancellationToken cancellationToken)
    {
        if (!_isOpen)
        {
            return null;
        }

        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM trigger_metadata WHERE key = 'legacy_migration_source_hash';";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult> TryCommitStateAsync(
        TriggerStateCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid(CreateNotOpenDiagnostic());
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryCommitStateCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable(
                CreateDiagnostic("trigger.storage.commit_failed", "commit_state", exception));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult<TriggerExecution>> TryCommitExecutionAsync(
        TriggerExecutionCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid<TriggerExecution>(CreateNotOpenDiagnostic());
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryCommitExecutionCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return TriggerPersistenceResult.Conflict<TriggerExecution>();
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable<TriggerExecution>(
                CreateDiagnostic("trigger.storage.commit_failed", "commit_execution", exception));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>>>
        ReadRecoverableActionsAsync(CancellationToken cancellationToken)
    {
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid<IReadOnlyList<TriggerOutboxAction>>(
                CreateNotOpenDiagnostic());
        }

        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(
                SqliteOpenMode.ReadWrite,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TriggerOutboxAction> actions = await ReadRecoverableActionsCoreAsync(
                connection,
                null,
                cancellationToken).ConfigureAwait(false);
            return TriggerPersistenceResult.Succeeded(actions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable<IReadOnlyList<TriggerOutboxAction>>(
                CreateDiagnostic("trigger.storage.read_failed", "read_outbox", exception));
        }
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult<TriggerOutboxAction>> TransitionOutboxAsync(
        TriggerOutboxTransition transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid<TriggerOutboxAction>(CreateNotOpenDiagnostic());
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TransitionOutboxCoreAsync(transition, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable<TriggerOutboxAction>(
                CreateDiagnostic("trigger.storage.write_failed", "transition_outbox", exception));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult<TriggerLifecycleHandoff>>
        TransitionLifecycleHandoffAsync(
            TriggerLifecycleHandoffTransition transition,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid<TriggerLifecycleHandoff>(CreateNotOpenDiagnostic());
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TransitionLifecycleHandoffCoreAsync(
                transition,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable<TriggerLifecycleHandoff>(
                CreateDiagnostic("trigger.storage.write_failed", "transition_handoff", exception));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<TriggerPersistenceResult> CreateBackupAsync(CancellationToken cancellationToken)
    {
        return CreateBackupWithGateAsync(cancellationToken);
    }

    private async Task<TriggerPersistenceResult> CreateBackupWithGateAsync(
        CancellationToken cancellationToken)
    {
        if (!_isOpen)
        {
            return TriggerPersistenceResult.Invalid(CreateNotOpenDiagnostic());
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateBackupCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return TriggerPersistenceResult.Unavailable(
                CreateDiagnostic("trigger.storage.backup_failed", "backup", exception));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(_busyTimeoutMilliseconds / 1000d)),
        };
        SqliteConnection connection = new(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = FormattableString.Invariant(
                $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds}; PRAGMA foreign_keys = ON;");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsExpectedStorageFailure(Exception exception)
    {
        return exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FormatException;
    }

    private static bool IsRecoverableCorruption(Exception exception)
    {
        return exception is InvalidDataException or FormatException
            || exception is SqliteException { SqliteErrorCode: 11 or 26 };
    }

    private async Task<TriggerRepositorySnapshot> OpenPrimaryAsync(
        CancellationToken cancellationToken)
    {
        bool existingDatabase = File.Exists(_databasePath)
            && new FileInfo(_databasePath).Length > 0;
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWriteCreate,
            cancellationToken).ConfigureAwait(false);
        if (existingDatabase)
        {
            await TriggerDatabaseSchema.PrepareExistingAsync(
                connection,
                enableWal: true,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await TriggerDatabaseSchema.InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        return await ReadVerifiedSnapshotCoreAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TriggerRepositorySnapshot> RecoverCorruptStorageAsync(
        CancellationToken cancellationToken)
    {
        TriggerBackupManager backupManager = new(
            _databasePath,
            _backupPath,
            _busyTimeoutMilliseconds,
            _faultInjector);
        if (File.Exists(_backupPath))
        {
            try
            {
                await backupManager.RestoreAsync(cancellationToken).ConfigureAwait(false);
                return await OpenRecoveredStorageAsync(
                    new TriggerDiagnostic(
                        "trigger.storage.recovered",
                        TriggerDiagnosticSeverity.Warning,
                        null,
                        "backup:restored",
                        DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableCorruption(exception))
            {
                // Both authorities are corrupt. They are quarantined below before safe initialization.
            }
        }

        TriggerBackupManager.QuarantineDatabaseFiles(_databasePath);
        TriggerBackupManager.QuarantineDatabaseFiles(_backupPath);
        return await OpenRecoveredStorageAsync(
            new TriggerDiagnostic(
                "trigger.storage.safe_empty",
                TriggerDiagnosticSeverity.Error,
                null,
                "primary_and_backup:invalid",
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TriggerRepositorySnapshot> OpenRecoveredStorageAsync(
        TriggerDiagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWriteCreate,
            cancellationToken).ConfigureAwait(false);
        await TriggerDatabaseSchema.InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO trigger_diagnostics(code, severity, task_id, detail, occurred_at)
                VALUES ($code, $severity, $taskId, $detail, $occurredAt);
                """;
            command.Parameters.AddWithValue("$code", diagnostic.Code);
            command.Parameters.AddWithValue("$severity", (int)diagnostic.Severity);
            command.Parameters.AddWithValue("$taskId", diagnostic.TaskId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$detail", diagnostic.Detail);
            command.Parameters.AddWithValue("$occurredAt", FormatTimestamp(diagnostic.OccurredAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ReadVerifiedSnapshotCoreAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
    }

    private static TriggerDiagnostic CreateNotOpenDiagnostic()
    {
        return new TriggerDiagnostic(
            "trigger.storage.not_open",
            TriggerDiagnosticSeverity.Error,
            null,
            "repository:not_open",
            DateTimeOffset.UtcNow);
    }

    private static TriggerDiagnostic CreateDiagnostic(
        string code,
        string operation,
        Exception exception)
    {
        return new TriggerDiagnostic(
            code,
            TriggerDiagnosticSeverity.Error,
            null,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{operation}:{exception.GetType().Name}"),
            DateTimeOffset.UtcNow);
    }
}
