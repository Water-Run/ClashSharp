using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Infrastructure.Triggers;

public sealed partial class SqliteTriggerRepository
{
    private async Task<TriggerPersistenceResult<TriggerExecution>> TryCommitExecutionCoreAsync(
        TriggerExecutionCommitRequest request,
        CancellationToken cancellationToken)
    {
        if (!StateMatchesDefinition(request.Definition, request.NextState))
        {
            return TriggerPersistenceResult.Invalid<TriggerExecution>(new TriggerDiagnostic(
                "trigger.state.invalid",
                TriggerDiagnosticSeverity.Error,
                request.Definition.Id,
                "commit:condition_state_mismatch",
                DateTimeOffset.UtcNow));
        }

        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        (long Revision, long Version)? current = await ReadTaskVersionAsync(
            connection,
            transaction,
            request.Definition.Id,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return TriggerPersistenceResult.NotFound<TriggerExecution>();
        }

        if (current.Value.Revision != request.Definition.Revision
            || current.Value.Version != request.ExpectedStateVersion)
        {
            return TriggerPersistenceResult.Conflict<TriggerExecution>();
        }

        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE trigger_states
            SET version = version + 1,
                last_triggered_at = $triggeredAt
            WHERE task_id = $taskId
              AND task_revision = $revision
              AND version = $expectedVersion;
            """))
        {
            command.Parameters.AddWithValue("$taskId", request.Definition.Id);
            command.Parameters.AddWithValue("$revision", request.Definition.Revision);
            command.Parameters.AddWithValue("$expectedVersion", request.ExpectedStateVersion);
            command.Parameters.AddWithValue("$triggeredAt", FormatTimestamp(request.TriggeredAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return TriggerPersistenceResult.Conflict<TriggerExecution>();
            }
        }

        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            "DELETE FROM trigger_condition_states WHERE task_id = $taskId;"))
        {
            command.Parameters.AddWithValue("$taskId", request.Definition.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach ((string conditionId, TriggerConditionState conditionState) in
            request.NextState.ConditionStates)
        {
            await InsertConditionStateAsync(
                connection,
                transaction,
                request.Definition.Id,
                conditionId,
                conditionState,
                cancellationToken).ConfigureAwait(false);
        }

        TriggerExecution execution = new(
            request.ExecutionId,
            request.Definition.Id,
            request.Definition.Revision,
            request.TriggeredAt,
            request.ProcessEpoch,
            TriggerExecutionState.Pending);
        await InsertExecutionAsync(
            connection,
            transaction,
            execution,
            cancellationToken).ConfigureAwait(false);
        foreach (TriggerOutboxAction action in request.OutboxActions)
        {
            await InsertOutboxActionAsync(
                connection,
                transaction,
                action,
                cancellationToken).ConfigureAwait(false);
        }

        await _faultInjector.InjectAsync(
            TriggerPersistenceFaultPoint.BeforeExecutionCommit,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        await _faultInjector.InjectAsync(
            TriggerPersistenceFaultPoint.AfterExecutionCommit,
            cancellationToken).ConfigureAwait(false);
        return TriggerPersistenceResult.Succeeded(execution);
    }

    private async Task<TriggerPersistenceResult<TriggerOutboxAction>> TransitionOutboxCoreAsync(
        TriggerOutboxTransition transition,
        CancellationToken cancellationToken)
    {
        if (!IsLegalOutboxTransition(transition.ExpectedState, transition.NextState))
        {
            return TriggerPersistenceResult.Invalid<TriggerOutboxAction>(new TriggerDiagnostic(
                "trigger.outbox.transition.invalid",
                TriggerDiagnosticSeverity.Error,
                null,
                "outbox:illegal_transition",
                DateTimeOffset.UtcNow));
        }

        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        TriggerOutboxAction? current = await ReadOutboxActionAsync(
            connection,
            transaction,
            transition.ExecutionId,
            transition.ActionIndex,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return TriggerPersistenceResult.NotFound<TriggerOutboxAction>();
        }

        if (current.State != transition.ExpectedState)
        {
            return TriggerPersistenceResult.Conflict<TriggerOutboxAction>();
        }

        int attemptCount = checked(current.AttemptCount
            + (transition.NextState == TriggerOutboxState.Running ? 1 : 0));
        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE trigger_outbox
            SET state = $nextState,
                attempt_count = $attemptCount,
                last_error = $lastError
            WHERE execution_id = $executionId
              AND action_index = $actionIndex
              AND state = $expectedState;
            """))
        {
            command.Parameters.AddWithValue("$nextState", (int)transition.NextState);
            command.Parameters.AddWithValue("$attemptCount", attemptCount);
            command.Parameters.AddWithValue(
                "$lastError",
                transition.LastError ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$executionId", transition.ExecutionId.ToString("N"));
            command.Parameters.AddWithValue("$actionIndex", transition.ActionIndex);
            command.Parameters.AddWithValue("$expectedState", (int)transition.ExpectedState);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return TriggerPersistenceResult.Conflict<TriggerOutboxAction>();
            }
        }

        await UpdateExecutionStateAsync(
            connection,
            transaction,
            transition.ExecutionId,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return TriggerPersistenceResult.Succeeded(new TriggerOutboxAction(
            current.ExecutionId,
            current.TaskRevision,
            current.ActionIndex,
            current.IdempotencyKey,
            current.DesiredEffect,
            transition.NextState,
            attemptCount,
            transition.LastError));
    }

    private async Task<TriggerPersistenceResult<TriggerLifecycleHandoff>>
        TransitionLifecycleHandoffCoreAsync(
            TriggerLifecycleHandoffTransition transition,
            CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ExecutionActionIdentity? identity = await ReadExecutionActionIdentityAsync(
            connection,
            transaction,
            transition.ExecutionId,
            transition.ActionIndex,
            cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return TriggerPersistenceResult.NotFound<TriggerLifecycleHandoff>();
        }

        if (identity.ProcessEpoch != transition.ProcessEpoch
            || identity.ActionKind != TriggerActionKind.ExitApplication)
        {
            return TriggerPersistenceResult.Invalid<TriggerLifecycleHandoff>(new TriggerDiagnostic(
                "trigger.handoff.identity.invalid",
                TriggerDiagnosticSeverity.Error,
                null,
                "handoff:identity_mismatch",
                DateTimeOffset.UtcNow));
        }

        TriggerLifecycleHandoff? current = await ReadLifecycleHandoffAsync(
            connection,
            transaction,
            transition.ExecutionId,
            transition.ActionIndex,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            if (transition.ExpectedState is not null
                || transition.NextState != TriggerLifecycleHandoffState.HandedOff)
            {
                return TriggerPersistenceResult.Conflict<TriggerLifecycleHandoff>();
            }

            if (identity.OutboxState != TriggerOutboxState.Running)
            {
                return TriggerPersistenceResult.Invalid<TriggerLifecycleHandoff>(new TriggerDiagnostic(
                    "trigger.handoff.outbox_state.invalid",
                    TriggerDiagnosticSeverity.Error,
                    null,
                    "handoff:outbox_not_running",
                    DateTimeOffset.UtcNow));
            }

            await InsertLifecycleHandoffAsync(
                connection,
                transaction,
                transition,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (transition.ExpectedState != current.State)
            {
                return TriggerPersistenceResult.Conflict<TriggerLifecycleHandoff>();
            }

            if (!IsLegalHandoffTransition(current.State, transition.NextState))
            {
                return TriggerPersistenceResult.Invalid<TriggerLifecycleHandoff>(new TriggerDiagnostic(
                    "trigger.handoff.transition.invalid",
                    TriggerDiagnosticSeverity.Error,
                    null,
                    "handoff:illegal_transition",
                    DateTimeOffset.UtcNow));
            }

            await UpdateLifecycleHandoffAsync(
                connection,
                transaction,
                transition,
                cancellationToken).ConfigureAwait(false);
        }

        TriggerOutboxState outboxState = ToOutboxState(transition.NextState);
        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE trigger_outbox
            SET state = $state,
                last_error = $lastError
            WHERE execution_id = $executionId AND action_index = $actionIndex;
            """))
        {
            command.Parameters.AddWithValue("$state", (int)outboxState);
            command.Parameters.AddWithValue(
                "$lastError",
                transition.LastError ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$executionId", transition.ExecutionId.ToString("N"));
            command.Parameters.AddWithValue("$actionIndex", transition.ActionIndex);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateExecutionStateAsync(
            connection,
            transaction,
            transition.ExecutionId,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return TriggerPersistenceResult.Succeeded(new TriggerLifecycleHandoff(
            transition.ExecutionId,
            transition.ActionIndex,
            transition.ProcessEpoch,
            transition.NextState,
            transition.UpdatedAt,
            transition.LastError));
    }

    private static async Task<(long Revision, long Version)?> ReadTaskVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            "SELECT task_revision, version FROM trigger_states WHERE task_id = $taskId;");
        command.Parameters.AddWithValue("$taskId", taskId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private static async Task InsertExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TriggerExecution execution,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO trigger_executions(
                execution_id, task_id, task_revision, triggered_at, process_epoch, state)
            VALUES ($executionId, $taskId, $revision, $triggeredAt, $processEpoch, $state);
            """);
        command.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$taskId", execution.TaskId);
        command.Parameters.AddWithValue("$revision", execution.TaskRevision);
        command.Parameters.AddWithValue("$triggeredAt", FormatTimestamp(execution.TriggeredAt));
        command.Parameters.AddWithValue("$processEpoch", execution.ProcessEpoch.ToString("N"));
        command.Parameters.AddWithValue("$state", (int)execution.State);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOutboxActionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TriggerOutboxAction action,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO trigger_outbox(
                execution_id, action_index, task_revision, idempotency_key,
                action_kind, parameters_json, state, attempt_count, last_error)
            VALUES (
                $executionId, $actionIndex, $revision, $idempotencyKey,
                $actionKind, $parameters, $state, $attemptCount, $lastError);
            """);
        command.Parameters.AddWithValue("$executionId", action.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$actionIndex", action.ActionIndex);
        command.Parameters.AddWithValue("$revision", action.TaskRevision);
        command.Parameters.AddWithValue("$idempotencyKey", action.IdempotencyKey);
        command.Parameters.AddWithValue("$actionKind", (int)action.DesiredEffect.Kind);
        command.Parameters.AddWithValue(
            "$parameters",
            TriggerDefinitionCodec.SerializeActionParameters(action.DesiredEffect));
        command.Parameters.AddWithValue("$state", (int)action.State);
        command.Parameters.AddWithValue("$attemptCount", action.AttemptCount);
        command.Parameters.AddWithValue("$lastError", action.LastError ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TriggerOutboxAction?> ReadOutboxActionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        int actionIndex,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT task_revision, idempotency_key, action_kind, parameters_json,
                   state, attempt_count, last_error
            FROM trigger_outbox
            WHERE execution_id = $executionId AND action_index = $actionIndex;
            """);
        command.Parameters.AddWithValue("$executionId", executionId.ToString("N"));
        command.Parameters.AddWithValue("$actionIndex", actionIndex);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        TriggerActionKind actionKind = (TriggerActionKind)reader.GetInt32(2);
        return new TriggerOutboxAction(
            executionId,
            reader.GetInt64(0),
            actionIndex,
            reader.GetString(1),
            new TriggerAction(
                actionKind,
                TriggerDefinitionCodec.DeserializeActionParameters(actionKind, reader.GetString(3))),
            (TriggerOutboxState)reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static async Task UpdateExecutionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        List<TriggerOutboxState> states = [];
        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            "SELECT state FROM trigger_outbox WHERE execution_id = $executionId;"))
        {
            command.Parameters.AddWithValue("$executionId", executionId.ToString("N"));
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                states.Add((TriggerOutboxState)reader.GetInt32(0));
            }
        }

        TriggerExecutionState aggregate = AggregateExecutionState(states);
        await using SqliteCommand update = CreateCommand(
            connection,
            transaction,
            "UPDATE trigger_executions SET state = $state WHERE execution_id = $executionId;");
        update.Parameters.AddWithValue("$state", (int)aggregate);
        update.Parameters.AddWithValue("$executionId", executionId.ToString("N"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExecutionActionIdentity?> ReadExecutionActionIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        int actionIndex,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT e.process_epoch, o.action_kind, o.state
            FROM trigger_executions AS e
            INNER JOIN trigger_outbox AS o ON o.execution_id = e.execution_id
            WHERE e.execution_id = $executionId AND o.action_index = $actionIndex;
            """);
        command.Parameters.AddWithValue("$executionId", executionId.ToString("N"));
        command.Parameters.AddWithValue("$actionIndex", actionIndex);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ExecutionActionIdentity(
                Guid.ParseExact(reader.GetString(0), "N"),
                (TriggerActionKind)reader.GetInt32(1),
                (TriggerOutboxState)reader.GetInt32(2))
            : null;
    }

    private static async Task<TriggerLifecycleHandoff?> ReadLifecycleHandoffAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        int actionIndex,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT process_epoch, state, updated_at, last_error
            FROM trigger_lifecycle_handoffs
            WHERE execution_id = $executionId AND action_index = $actionIndex;
            """);
        command.Parameters.AddWithValue("$executionId", executionId.ToString("N"));
        command.Parameters.AddWithValue("$actionIndex", actionIndex);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new TriggerLifecycleHandoff(
                executionId,
                actionIndex,
                Guid.ParseExact(reader.GetString(0), "N"),
                (TriggerLifecycleHandoffState)reader.GetInt32(1),
                ParseTimestamp(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static async Task InsertLifecycleHandoffAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TriggerLifecycleHandoffTransition transition,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO trigger_lifecycle_handoffs(
                execution_id, action_index, process_epoch, state, updated_at, last_error)
            VALUES ($executionId, $actionIndex, $processEpoch, $state, $updatedAt, $lastError);
            """);
        AddLifecycleParameters(command, transition);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateLifecycleHandoffAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TriggerLifecycleHandoffTransition transition,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE trigger_lifecycle_handoffs
            SET state = $state, updated_at = $updatedAt, last_error = $lastError
            WHERE execution_id = $executionId
              AND action_index = $actionIndex
              AND process_epoch = $processEpoch;
            """);
        AddLifecycleParameters(command, transition);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddLifecycleParameters(
        SqliteCommand command,
        TriggerLifecycleHandoffTransition transition)
    {
        command.Parameters.AddWithValue("$executionId", transition.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$actionIndex", transition.ActionIndex);
        command.Parameters.AddWithValue("$processEpoch", transition.ProcessEpoch.ToString("N"));
        command.Parameters.AddWithValue("$state", (int)transition.NextState);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(transition.UpdatedAt));
        command.Parameters.AddWithValue("$lastError", transition.LastError ?? (object)DBNull.Value);
    }

    private static bool StateMatchesDefinition(
        TriggerTaskDefinition definition,
        TriggerTaskState state)
    {
        return definition.Conditions.Count == state.ConditionStates.Count
            && definition.Conditions.All(
                condition => state.ConditionStates.ContainsKey(condition.Id));
    }

    private static bool IsLegalOutboxTransition(
        TriggerOutboxState current,
        TriggerOutboxState next)
    {
        return (current, next) switch
        {
            (TriggerOutboxState.Pending, TriggerOutboxState.Running) => true,
            (TriggerOutboxState.Running, TriggerOutboxState.Succeeded) => true,
            (TriggerOutboxState.Running, TriggerOutboxState.Pending) => true,
            (TriggerOutboxState.Running, TriggerOutboxState.Failed) => true,
            (TriggerOutboxState.Running, TriggerOutboxState.Uncertain) => true,
            (TriggerOutboxState.Running, TriggerOutboxState.HandedOff) => true,
            (TriggerOutboxState.Failed, TriggerOutboxState.Pending) => true,
            (TriggerOutboxState.HandedOff, TriggerOutboxState.Succeeded) => true,
            (TriggerOutboxState.HandedOff, TriggerOutboxState.Failed) => true,
            (TriggerOutboxState.HandedOff, TriggerOutboxState.Uncertain) => true,
            _ => false,
        };
    }

    private static bool IsLegalHandoffTransition(
        TriggerLifecycleHandoffState current,
        TriggerLifecycleHandoffState next)
    {
        return (current, next) switch
        {
            (TriggerLifecycleHandoffState.HandedOff,
                TriggerLifecycleHandoffState.ReleaseAcknowledged) => true,
            (TriggerLifecycleHandoffState.HandedOff,
                TriggerLifecycleHandoffState.Succeeded) => true,
            (TriggerLifecycleHandoffState.ReleaseAcknowledged,
                TriggerLifecycleHandoffState.ShutdownStarted) => true,
            (TriggerLifecycleHandoffState.ShutdownStarted,
                TriggerLifecycleHandoffState.Succeeded) => true,
            (TriggerLifecycleHandoffState.ShutdownStarted,
                TriggerLifecycleHandoffState.Failed) => true,
            (TriggerLifecycleHandoffState.ShutdownStarted,
                TriggerLifecycleHandoffState.Uncertain) => true,
            _ => false,
        };
    }

    private static TriggerExecutionState AggregateExecutionState(
        IReadOnlyCollection<TriggerOutboxState> states)
    {
        if (states.Count == 0 || states.All(state => state == TriggerOutboxState.Succeeded))
        {
            return TriggerExecutionState.Succeeded;
        }

        if (states.Contains(TriggerOutboxState.Uncertain))
        {
            return TriggerExecutionState.Uncertain;
        }

        if (states.Contains(TriggerOutboxState.Failed))
        {
            return TriggerExecutionState.Failed;
        }

        if (states.Contains(TriggerOutboxState.HandedOff))
        {
            return TriggerExecutionState.HandedOff;
        }

        return states.Contains(TriggerOutboxState.Running)
            ? TriggerExecutionState.Running
            : TriggerExecutionState.Pending;
    }

    private static TriggerOutboxState ToOutboxState(TriggerLifecycleHandoffState state)
    {
        return state switch
        {
            TriggerLifecycleHandoffState.HandedOff or
                TriggerLifecycleHandoffState.ReleaseAcknowledged or
                TriggerLifecycleHandoffState.ShutdownStarted => TriggerOutboxState.HandedOff,
            TriggerLifecycleHandoffState.Succeeded => TriggerOutboxState.Succeeded,
            TriggerLifecycleHandoffState.Failed => TriggerOutboxState.Failed,
            TriggerLifecycleHandoffState.Uncertain => TriggerOutboxState.Uncertain,
            _ => throw new InvalidDataException("Undefined lifecycle handoff state."),
        };
    }

    private sealed record ExecutionActionIdentity(
        Guid ProcessEpoch,
        TriggerActionKind ActionKind,
        TriggerOutboxState OutboxState);
}
