using System.Globalization;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Infrastructure.Triggers;

public sealed partial class SqliteTriggerRepository
{
    private async Task<TriggerPersistenceResult> ReplaceDefinitionsCoreAsync(
        TriggerDefinitionWriteRequest request,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        TriggerRepositorySnapshot current = await ReadSnapshotCoreAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (current.DefinitionGeneration != request.ExpectedGeneration)
        {
            return TriggerPersistenceResult.Conflict();
        }

        Dictionary<string, TriggerTaskRecord> currentById = current.Tasks.ToDictionary(
            record => record.Definition.Id,
            StringComparer.Ordinal);
        foreach (TriggerTaskDefinition definition in request.Definitions)
        {
            if (!currentById.TryGetValue(definition.Id, out TriggerTaskRecord? existing))
            {
                continue;
            }

            if (definition.Revision < existing.Definition.Revision
                || (definition.Revision == existing.Definition.Revision
                    && !DefinitionsEqual(definition, existing.Definition)))
            {
                return TriggerPersistenceResult.Invalid(new TriggerDiagnostic(
                    "trigger.definition.revision_conflict",
                    TriggerDiagnosticSeverity.Error,
                    definition.Id,
                    "definition:revision_conflict",
                    DateTimeOffset.UtcNow));
            }
        }

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "DELETE FROM trigger_tasks;",
            cancellationToken).ConfigureAwait(false);
        for (int order = 0; order < request.Definitions.Count; order++)
        {
            TriggerTaskDefinition definition = request.Definitions[order];
            TriggerTaskState state = currentById.TryGetValue(definition.Id, out TriggerTaskRecord? existing)
                && definition.Revision == existing.Definition.Revision
                    ? existing.State
                    : TriggerTaskState.CreateInitial(definition);
            await InsertDefinitionAsync(
                connection,
                transaction,
                definition,
                state,
                order,
                cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            "UPDATE trigger_metadata SET value = $value WHERE key = 'definition_generation';"))
        {
            command.Parameters.AddWithValue(
                "$value",
                checked(current.DefinitionGeneration + 1).ToString(CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("Definition generation metadata is missing.");
            }
        }

        transaction.Commit();
        return TriggerPersistenceResult.Succeeded();
    }

    private static async Task InsertDefinitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TriggerTaskDefinition definition,
        TriggerTaskState state,
        int order,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO trigger_tasks(task_id, revision, name, is_enabled, sort_order)
            VALUES ($taskId, $revision, $name, $enabled, $order);
            """))
        {
            command.Parameters.AddWithValue("$taskId", definition.Id);
            command.Parameters.AddWithValue("$revision", definition.Revision);
            command.Parameters.AddWithValue("$name", definition.Name);
            command.Parameters.AddWithValue("$enabled", definition.IsEnabled);
            command.Parameters.AddWithValue("$order", order);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (int conditionIndex = 0; conditionIndex < definition.Conditions.Count; conditionIndex++)
        {
            TriggerCondition condition = definition.Conditions[conditionIndex];
            await using SqliteCommand command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO trigger_conditions(
                    task_id, condition_id, kind, parameters_json, sort_order)
                VALUES ($taskId, $conditionId, $kind, $parameters, $order);
                """);
            command.Parameters.AddWithValue("$taskId", definition.Id);
            command.Parameters.AddWithValue("$conditionId", condition.Id);
            command.Parameters.AddWithValue("$kind", (int)condition.Kind);
            command.Parameters.AddWithValue(
                "$parameters",
                TriggerDefinitionCodec.SerializeConditionParameters(condition));
            command.Parameters.AddWithValue("$order", conditionIndex);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (int actionIndex = 0; actionIndex < definition.Actions.Count; actionIndex++)
        {
            TriggerAction action = definition.Actions[actionIndex];
            await using SqliteCommand command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO trigger_actions(task_id, action_index, kind, parameters_json)
                VALUES ($taskId, $actionIndex, $kind, $parameters);
                """);
            command.Parameters.AddWithValue("$taskId", definition.Id);
            command.Parameters.AddWithValue("$actionIndex", actionIndex);
            command.Parameters.AddWithValue("$kind", (int)action.Kind);
            command.Parameters.AddWithValue(
                "$parameters",
                TriggerDefinitionCodec.SerializeActionParameters(action));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO trigger_states(task_id, task_revision, version)
            VALUES ($taskId, $revision, $version);
            """))
        {
            command.Parameters.AddWithValue("$taskId", state.TaskId);
            command.Parameters.AddWithValue("$revision", state.TaskRevision);
            command.Parameters.AddWithValue("$version", state.Version);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach ((string conditionId, TriggerConditionState conditionState) in state.ConditionStates)
        {
            await InsertConditionStateAsync(
                connection,
                transaction,
                state.TaskId,
                conditionId,
                conditionState,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertConditionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string conditionId,
        TriggerConditionState state,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO trigger_condition_states(
                task_id, condition_id, is_armed, consumed_date, consumed_revision)
            VALUES ($taskId, $conditionId, $isArmed, $consumedDate, $consumedRevision);
            """);
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$conditionId", conditionId);
        command.Parameters.AddWithValue("$isArmed", state.IsArmed);
        command.Parameters.AddWithValue(
            "$consumedDate",
            state.ConsumedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$consumedRevision",
            state.ConsumedRevision ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool DefinitionsEqual(
        TriggerTaskDefinition left,
        TriggerTaskDefinition right)
    {
        return StringComparer.Ordinal.Equals(left.Id, right.Id)
            && left.Revision == right.Revision
            && StringComparer.Ordinal.Equals(left.Name, right.Name)
            && left.IsEnabled == right.IsEnabled
            && left.Conditions.SequenceEqual(right.Conditions)
            && left.Actions.SequenceEqual(right.Actions);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(connection, transaction, commandText);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
