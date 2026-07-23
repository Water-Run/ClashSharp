using System.Collections.ObjectModel;
using System.Globalization;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Infrastructure.Triggers;

public sealed partial class SqliteTriggerRepository
{
    private async Task<TriggerRepositorySnapshot> ReadVerifiedSnapshotCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        TriggerRepositorySnapshot snapshot = await ReadSnapshotCoreAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ValidateDurableRowsAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return snapshot;
    }

    private async Task<TriggerRepositorySnapshot> ReadSnapshotCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            if (transaction is not null)
            {
                return await ReadSnapshotWithinTransactionAsync(
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            using SqliteTransaction readTransaction = connection.BeginTransaction(deferred: true);
            TriggerRepositorySnapshot snapshot = await ReadSnapshotWithinTransactionAsync(
                connection,
                readTransaction,
                cancellationToken).ConfigureAwait(false);
            readTransaction.Commit();
            return snapshot;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Stored trigger data violates domain invariants.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Stored trigger data exceeds supported ranges.", exception);
        }
    }

    private static async Task<TriggerRepositorySnapshot> ReadSnapshotWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        int schemaVersion = checked((int)await ReadMetadataLongAsync(
            connection,
            transaction,
            "schema_version",
            cancellationToken).ConfigureAwait(false));
        long generation = await ReadMetadataLongAsync(
            connection,
            transaction,
            "definition_generation",
            cancellationToken).ConfigureAwait(false);
        List<TaskHeader> headers = await ReadTaskHeadersAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        List<TriggerTaskRecord> records = new(headers.Count);
        foreach (TaskHeader header in headers)
        {
            List<TriggerCondition> conditions = await ReadConditionsAsync(
                connection,
                transaction,
                header.TaskId,
                cancellationToken).ConfigureAwait(false);
            List<TriggerAction> actions = await ReadDefinitionActionsAsync(
                connection,
                transaction,
                header.TaskId,
                cancellationToken).ConfigureAwait(false);
            TriggerTaskDefinition definition = new(
                header.TaskId,
                header.Revision,
                header.Name,
                header.IsEnabled,
                conditions,
                actions);
            if (!TriggerDefinitionValidator.Validate(definition).IsValid)
            {
                throw new InvalidDataException("Stored trigger definition is invalid.");
            }

            TriggerTaskState state = await ReadTaskStateAsync(
                connection,
                transaction,
                definition,
                cancellationToken).ConfigureAwait(false);
            records.Add(new TriggerTaskRecord(header.Order, definition, state));
        }

        List<TriggerDiagnostic> diagnostics = await ReadDiagnosticsAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        return new TriggerRepositorySnapshot(schemaVersion, generation, records, diagnostics);
    }

    private static async Task<long> ReadMetadataLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            "SELECT value FROM trigger_metadata WHERE key = $key;");
        command.Parameters.AddWithValue("$key", key);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string text
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            || parsed < 0)
        {
            throw new InvalidDataException("Trigger metadata is missing or malformed.");
        }

        return parsed;
    }

    private static async Task<List<TaskHeader>> ReadTaskHeadersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT task_id, revision, name, is_enabled, sort_order
            FROM trigger_tasks
            ORDER BY sort_order;
            """);
        List<TaskHeader> headers = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            headers.Add(new TaskHeader(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetInt32(4)));
        }

        return headers;
    }

    private static async Task<List<TriggerCondition>> ReadConditionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT condition_id, kind, parameters_json
            FROM trigger_conditions
            WHERE task_id = $taskId
            ORDER BY sort_order;
            """);
        command.Parameters.AddWithValue("$taskId", taskId);
        List<TriggerCondition> conditions = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            TriggerConditionKind kind = (TriggerConditionKind)reader.GetInt32(1);
            conditions.Add(new TriggerCondition(
                reader.GetString(0),
                kind,
                TriggerDefinitionCodec.DeserializeConditionParameters(kind, reader.GetString(2))));
        }

        return conditions;
    }

    private static async Task<List<TriggerAction>> ReadDefinitionActionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT kind, parameters_json
            FROM trigger_actions
            WHERE task_id = $taskId
            ORDER BY action_index;
            """);
        command.Parameters.AddWithValue("$taskId", taskId);
        List<TriggerAction> actions = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            TriggerActionKind kind = (TriggerActionKind)reader.GetInt32(0);
            actions.Add(new TriggerAction(
                kind,
                TriggerDefinitionCodec.DeserializeActionParameters(kind, reader.GetString(1))));
        }

        return actions;
    }

    private static async Task<TriggerTaskState> ReadTaskStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TriggerTaskDefinition definition,
        CancellationToken cancellationToken)
    {
        long taskRevision;
        long version;
        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            "SELECT task_revision, version FROM trigger_states WHERE task_id = $taskId;"))
        {
            command.Parameters.AddWithValue("$taskId", definition.Id);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Stored trigger state is missing.");
            }

            taskRevision = reader.GetInt64(0);
            version = reader.GetInt64(1);
        }

        Dictionary<string, TriggerConditionState> conditionStates = new(StringComparer.Ordinal);
        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT condition_id, is_armed, consumed_date, consumed_revision
            FROM trigger_condition_states
            WHERE task_id = $taskId
            ORDER BY condition_id;
            """))
        {
            command.Parameters.AddWithValue("$taskId", definition.Id);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                DateOnly? consumedDate = reader.IsDBNull(2)
                    ? null
                    : DateOnly.ParseExact(
                        reader.GetString(2),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                long? consumedRevision = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                conditionStates.Add(
                    reader.GetString(0),
                    new TriggerConditionState(reader.GetBoolean(1), consumedDate, consumedRevision));
            }
        }

        return new TriggerTaskState(definition.Id, taskRevision, version, conditionStates);
    }

    private static async Task<List<TriggerDiagnostic>> ReadDiagnosticsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT code, severity, task_id, detail, occurred_at
            FROM trigger_diagnostics
            ORDER BY diagnostic_id;
            """);
        List<TriggerDiagnostic> diagnostics = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            diagnostics.Add(new TriggerDiagnostic(
                reader.GetString(0),
                (TriggerDiagnosticSeverity)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                ParseTimestamp(reader.GetString(4))));
        }

        return diagnostics;
    }

    private static async Task<IReadOnlyList<TriggerOutboxAction>> ReadRecoverableActionsCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT o.execution_id, o.task_revision, o.action_index, o.idempotency_key,
                   o.action_kind, o.parameters_json, o.state, o.attempt_count, o.last_error
            FROM trigger_outbox AS o
            INNER JOIN trigger_executions AS e ON e.execution_id = o.execution_id
            WHERE o.state IN (0, 1, 2)
            ORDER BY e.triggered_at, e.execution_id, o.action_index;
            """);
        List<TriggerOutboxAction> actions = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            TriggerActionKind actionKind = (TriggerActionKind)reader.GetInt32(4);
            actions.Add(new TriggerOutboxAction(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetString(3),
                new TriggerAction(
                    actionKind,
                    TriggerDefinitionCodec.DeserializeActionParameters(
                        actionKind,
                        reader.GetString(5))),
                (TriggerOutboxState)reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return new ReadOnlyCollection<TriggerOutboxAction>(actions);
    }

    private static async Task ValidateDurableRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            Dictionary<Guid, TriggerExecution> executions = await ReadExecutionsForValidationAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            Dictionary<(Guid ExecutionId, int ActionIndex), TriggerOutboxAction> actions =
                await ReadOutboxForValidationAsync(
                    connection,
                    transaction,
                    executions,
                    cancellationToken).ConfigureAwait(false);
            await ValidateHandoffsAsync(
                connection,
                transaction,
                executions,
                actions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidDataException(
                "Durable trigger execution data violates domain invariants.",
                exception);
        }
    }

    private static async Task<Dictionary<Guid, TriggerExecution>> ReadExecutionsForValidationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT execution_id, task_id, task_revision, triggered_at, process_epoch, state
            FROM trigger_executions;
            """);
        Dictionary<Guid, TriggerExecution> executions = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid executionId = Guid.ParseExact(reader.GetString(0), "N");
            TriggerExecution execution = new(
                executionId,
                reader.GetString(1),
                reader.GetInt64(2),
                ParseTimestamp(reader.GetString(3)),
                Guid.ParseExact(reader.GetString(4), "N"),
                (TriggerExecutionState)reader.GetInt32(5));
            if (!executions.TryAdd(executionId, execution))
            {
                throw new InvalidDataException("Duplicate durable trigger execution identity.");
            }
        }

        return executions;
    }

    private static async Task<Dictionary<(Guid ExecutionId, int ActionIndex), TriggerOutboxAction>>
        ReadOutboxForValidationAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyDictionary<Guid, TriggerExecution> executions,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT execution_id, action_index, task_revision, idempotency_key,
                   action_kind, parameters_json, state, attempt_count, last_error
            FROM trigger_outbox
            ORDER BY execution_id, action_index;
            """);
        Dictionary<(Guid ExecutionId, int ActionIndex), TriggerOutboxAction> actions = [];
        Dictionary<Guid, List<TriggerOutboxState>> statesByExecution = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid executionId = Guid.ParseExact(reader.GetString(0), "N");
            int actionIndex = reader.GetInt32(1);
            TriggerActionKind kind = (TriggerActionKind)reader.GetInt32(4);
            TriggerOutboxAction action = new(
                executionId,
                reader.GetInt64(2),
                actionIndex,
                reader.GetString(3),
                new TriggerAction(
                    kind,
                    TriggerDefinitionCodec.DeserializeActionParameters(kind, reader.GetString(5))),
                (TriggerOutboxState)reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8));
            if (!executions.TryGetValue(executionId, out TriggerExecution? execution)
                || action.TaskRevision != execution.TaskRevision
                || !actions.TryAdd((executionId, actionIndex), action))
            {
                throw new InvalidDataException("Durable trigger outbox identity is inconsistent.");
            }

            if (!statesByExecution.TryGetValue(executionId, out List<TriggerOutboxState>? states))
            {
                states = [];
                statesByExecution.Add(executionId, states);
            }

            if (actionIndex != states.Count)
            {
                throw new InvalidDataException("Durable trigger outbox order is not contiguous.");
            }

            states.Add(action.State);
        }

        foreach ((Guid executionId, TriggerExecution execution) in executions)
        {
            if (!statesByExecution.TryGetValue(executionId, out List<TriggerOutboxState>? states)
                || states.Count == 0
                || AggregateExecutionState(states) != execution.State)
            {
                throw new InvalidDataException("Durable trigger execution aggregate is inconsistent.");
            }
        }

        return actions;
    }

    private static async Task ValidateHandoffsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<Guid, TriggerExecution> executions,
        IReadOnlyDictionary<(Guid ExecutionId, int ActionIndex), TriggerOutboxAction> actions,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT execution_id, action_index, process_epoch, state, updated_at, last_error
            FROM trigger_lifecycle_handoffs;
            """);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid executionId = Guid.ParseExact(reader.GetString(0), "N");
            int actionIndex = reader.GetInt32(1);
            TriggerLifecycleHandoff handoff = new(
                executionId,
                actionIndex,
                Guid.ParseExact(reader.GetString(2), "N"),
                (TriggerLifecycleHandoffState)reader.GetInt32(3),
                ParseTimestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5));
            if (!executions.TryGetValue(executionId, out TriggerExecution? execution)
                || !actions.TryGetValue((executionId, actionIndex), out TriggerOutboxAction? action)
                || handoff.ProcessEpoch != execution.ProcessEpoch
                || action.DesiredEffect.Kind != TriggerActionKind.ExitApplication
                || ToOutboxState(handoff.State) != action.State)
            {
                throw new InvalidDataException("Durable trigger lifecycle handoff is inconsistent.");
            }
        }
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    private sealed record TaskHeader(
        string TaskId,
        long Revision,
        string Name,
        bool IsEnabled,
        int Order);
}
