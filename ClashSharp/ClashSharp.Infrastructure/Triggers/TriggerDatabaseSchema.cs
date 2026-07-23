using Microsoft.Data.Sqlite;

namespace ClashSharp.Infrastructure.Triggers;

/// <summary>Owns the normalized trigger SQLite schema and version check.</summary>
public static class TriggerDatabaseSchema
{
    /// <summary>Gets the only schema version understood by this build.</summary>
    public const int CurrentVersion = 2;

    internal static async Task InitializeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await ExecuteNonQueryAsync(
            connection,
            null,
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS trigger_metadata (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_tasks (
                task_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(task_id)) > 0),
                revision INTEGER NOT NULL CHECK (revision > 0),
                name TEXT NOT NULL CHECK (length(trim(name)) > 0),
                is_enabled INTEGER NOT NULL CHECK (is_enabled IN (0, 1)),
                sort_order INTEGER NOT NULL UNIQUE CHECK (sort_order >= 0)
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_conditions (
                task_id TEXT NOT NULL,
                condition_id TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK (kind BETWEEN 0 AND 6),
                parameters_json TEXT NOT NULL CHECK (length(parameters_json) > 0),
                sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
                PRIMARY KEY (task_id, condition_id),
                UNIQUE (task_id, sort_order),
                FOREIGN KEY (task_id) REFERENCES trigger_tasks(task_id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_actions (
                task_id TEXT NOT NULL,
                action_index INTEGER NOT NULL CHECK (action_index >= 0),
                kind INTEGER NOT NULL CHECK (kind BETWEEN 0 AND 6),
                parameters_json TEXT NOT NULL CHECK (length(parameters_json) > 0),
                PRIMARY KEY (task_id, action_index),
                FOREIGN KEY (task_id) REFERENCES trigger_tasks(task_id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_states (
                task_id TEXT NOT NULL PRIMARY KEY,
                task_revision INTEGER NOT NULL CHECK (task_revision > 0),
                version INTEGER NOT NULL CHECK (version >= 0),
                last_triggered_at TEXT NULL,
                FOREIGN KEY (task_id) REFERENCES trigger_tasks(task_id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_condition_states (
                task_id TEXT NOT NULL,
                condition_id TEXT NOT NULL,
                is_armed INTEGER NOT NULL CHECK (is_armed IN (0, 1)),
                consumed_date TEXT NULL,
                consumed_revision INTEGER NULL CHECK (consumed_revision IS NULL OR consumed_revision > 0),
                PRIMARY KEY (task_id, condition_id),
                FOREIGN KEY (task_id) REFERENCES trigger_states(task_id) ON DELETE CASCADE,
                FOREIGN KEY (task_id, condition_id)
                    REFERENCES trigger_conditions(task_id, condition_id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_executions (
                execution_id TEXT NOT NULL PRIMARY KEY CHECK (length(execution_id) = 32),
                task_id TEXT NOT NULL CHECK (length(trim(task_id)) > 0),
                task_revision INTEGER NOT NULL CHECK (task_revision > 0),
                triggered_at TEXT NOT NULL,
                process_epoch TEXT NOT NULL CHECK (length(process_epoch) = 32),
                state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 5)
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_outbox (
                execution_id TEXT NOT NULL,
                action_index INTEGER NOT NULL CHECK (action_index >= 0),
                task_revision INTEGER NOT NULL CHECK (task_revision > 0),
                idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) > 0),
                action_kind INTEGER NOT NULL CHECK (action_kind BETWEEN 0 AND 6),
                parameters_json TEXT NOT NULL CHECK (length(parameters_json) > 0),
                state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 5),
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                last_error TEXT NULL,
                PRIMARY KEY (execution_id, action_index),
                FOREIGN KEY (execution_id)
                    REFERENCES trigger_executions(execution_id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_lifecycle_handoffs (
                execution_id TEXT NOT NULL,
                action_index INTEGER NOT NULL CHECK (action_index >= 0),
                process_epoch TEXT NOT NULL CHECK (length(process_epoch) = 32),
                state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 5),
                updated_at TEXT NOT NULL,
                last_error TEXT NULL,
                PRIMARY KEY (execution_id, action_index),
                FOREIGN KEY (execution_id, action_index)
                    REFERENCES trigger_outbox(execution_id, action_index) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS trigger_diagnostics (
                diagnostic_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                code TEXT NOT NULL CHECK (length(trim(code)) > 0),
                severity INTEGER NOT NULL CHECK (severity IN (0, 1, 2)),
                task_id TEXT NULL CHECK (task_id IS NULL OR length(trim(task_id)) > 0),
                detail TEXT NOT NULL CHECK (length(trim(detail)) > 0),
                occurred_at TEXT NOT NULL
            ) STRICT;

            CREATE INDEX IF NOT EXISTS ix_trigger_executions_recovery_order
                ON trigger_executions(triggered_at, execution_id);
            CREATE INDEX IF NOT EXISTS ix_trigger_outbox_recoverable
                ON trigger_outbox(state, execution_id, action_index);

            INSERT OR IGNORE INTO trigger_metadata(key, value)
                VALUES ('schema_version', '2');
            INSERT OR IGNORE INTO trigger_metadata(key, value)
                VALUES ('definition_generation', '0');
            """,
            cancellationToken).ConfigureAwait(false);

        string version = await ExecuteScalarStringAsync(
            connection,
            null,
            "SELECT value FROM trigger_metadata WHERE key = 'schema_version';",
            cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(version, out int parsedVersion) || parsedVersion != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported trigger schema version '{version}'.");
        }
    }

    internal static async Task PrepareExistingAsync(
        SqliteConnection connection,
        bool enableWal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await ExecuteNonQueryAsync(
            connection,
            null,
            enableWal
                ? "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;"
                : "PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;",
            cancellationToken).ConfigureAwait(false);
        await ValidateIntegrityAndTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        string versionText = await ExecuteScalarStringAsync(
            connection,
            null,
            "SELECT value FROM trigger_metadata WHERE key = 'schema_version';",
            cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(versionText, out int version))
        {
            throw new InvalidDataException("Trigger schema version is malformed.");
        }

        if (version == 1)
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                ALTER TABLE trigger_states ADD COLUMN last_triggered_at TEXT NULL;
                UPDATE trigger_metadata SET value = '2' WHERE key = 'schema_version';
                """,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        else if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported trigger schema version '{versionText}'.");
        }

        await ValidateAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ValidateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateIntegrityAndTablesAsync(connection, cancellationToken).ConfigureAwait(false);

        string version = await ExecuteScalarStringAsync(
            connection,
            null,
            "SELECT value FROM trigger_metadata WHERE key = 'schema_version';",
            cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(version, out int parsedVersion) || parsedVersion != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported trigger schema version '{version}'.");
        }
    }

    private static async Task ValidateIntegrityAndTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        string integrity = await ExecuteScalarStringAsync(
            connection,
            null,
            "PRAGMA integrity_check;",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Trigger database integrity check failed.");
        }

        long tableCount = await ExecuteScalarLongAsync(
            connection,
            """
            SELECT count(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN (
                'trigger_metadata', 'trigger_tasks', 'trigger_conditions', 'trigger_actions',
                'trigger_states', 'trigger_condition_states', 'trigger_executions',
                'trigger_outbox', 'trigger_lifecycle_handoffs', 'trigger_diagnostics');
            """,
            cancellationToken).ConfigureAwait(false);
        if (tableCount != 10)
        {
            throw new InvalidDataException("Trigger database schema is incomplete.");
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string
            ?? throw new InvalidDataException("Trigger database metadata is missing.");
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long count
            ? count
            : throw new InvalidDataException("Trigger database schema metadata is malformed.");
    }
}
