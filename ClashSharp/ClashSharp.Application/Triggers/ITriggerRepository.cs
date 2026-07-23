using System.Collections.ObjectModel;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Immutable request to commit one non-firing latch transition optimistically.</summary>
public sealed class TriggerStateCommitRequest
{
    /// <summary>Initializes one validated state-only commit request.</summary>
    /// <param name="definition">Validated source definition.</param>
    /// <param name="expectedStateVersion">State version that must still be authoritative.</param>
    /// <param name="nextState">Complete proposed next latch state.</param>
    public TriggerStateCommitRequest(
        TriggerTaskDefinition definition,
        long expectedStateVersion,
        TriggerTaskState nextState)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedStateVersion);
        ArgumentNullException.ThrowIfNull(nextState);
        if (!TriggerDefinitionValidator.Validate(definition).IsValid
            || !StringComparer.Ordinal.Equals(definition.Id, nextState.TaskId)
            || definition.Revision != nextState.TaskRevision
            || expectedStateVersion != nextState.Version
            || definition.Conditions.Count != nextState.ConditionStates.Count
            || definition.Conditions.Any(
                condition => !nextState.ConditionStates.ContainsKey(condition.Id)))
        {
            throw new ArgumentException(
                "Definition, expected state version, and next state must describe one valid task revision.",
                nameof(nextState));
        }

        Definition = definition;
        ExpectedStateVersion = expectedStateVersion;
        NextState = nextState;
    }

    /// <summary>Gets the validated source definition.</summary>
    public TriggerTaskDefinition Definition { get; }

    /// <summary>Gets the state version that must still be authoritative.</summary>
    public long ExpectedStateVersion { get; }

    /// <summary>Gets the complete proposed next latch state.</summary>
    public TriggerTaskState NextState { get; }
}

/// <summary>Immutable request to commit a match state and its complete ordered action outbox atomically.</summary>
public sealed class TriggerExecutionCommitRequest
{
    /// <summary>Initializes an atomic execution commit request.</summary>
    /// <param name="executionId">Nonempty execution identity.</param>
    /// <param name="definition">Validated source definition.</param>
    /// <param name="expectedStateVersion">State version that must still be authoritative.</param>
    /// <param name="nextState">Complete proposed next latch state.</param>
    /// <param name="triggeredAt">Timestamp of the match.</param>
    /// <param name="processEpoch">Nonempty current process epoch.</param>
    public TriggerExecutionCommitRequest(
        Guid executionId,
        TriggerTaskDefinition definition,
        long expectedStateVersion,
        TriggerTaskState nextState,
        DateTimeOffset triggeredAt,
        Guid processEpoch)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("Execution identity must be nonempty.", nameof(executionId));
        }

        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedStateVersion);
        ArgumentNullException.ThrowIfNull(nextState);
        if (!TriggerDefinitionValidator.Validate(definition).IsValid
            || !StringComparer.Ordinal.Equals(definition.Id, nextState.TaskId)
            || definition.Revision != nextState.TaskRevision
            || expectedStateVersion != nextState.Version
            || definition.Conditions.Count != nextState.ConditionStates.Count
            || definition.Conditions.Any(
                condition => !nextState.ConditionStates.ContainsKey(condition.Id)))
        {
            throw new ArgumentException(
                "Definition, expected state version, and next state must describe one valid task revision.",
                nameof(nextState));
        }

        if (processEpoch == Guid.Empty)
        {
            throw new ArgumentException("Process epoch must be nonempty.", nameof(processEpoch));
        }

        ExecutionId = executionId;
        Definition = definition;
        ExpectedStateVersion = expectedStateVersion;
        NextState = nextState;
        TriggeredAt = triggeredAt;
        ProcessEpoch = processEpoch;
        OutboxActions = Array.AsReadOnly(definition.Actions
            .Select((action, index) => new TriggerOutboxAction(
                executionId,
                definition.Revision,
                index,
                TriggerIdempotencyKey.Create(executionId, definition.Revision, index),
                action,
                TriggerOutboxState.Pending))
            .ToArray());
    }

    /// <summary>Gets the execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the validated source definition.</summary>
    public TriggerTaskDefinition Definition { get; }

    /// <summary>Gets the state version that must still be authoritative.</summary>
    public long ExpectedStateVersion { get; }

    /// <summary>Gets the complete proposed next latch state.</summary>
    public TriggerTaskState NextState { get; }

    /// <summary>Gets the timestamp of the match.</summary>
    public DateTimeOffset TriggeredAt { get; }

    /// <summary>Gets the current process epoch.</summary>
    public Guid ProcessEpoch { get; }

    /// <summary>Gets the complete deterministic pending outbox.</summary>
    public ReadOnlyCollection<TriggerOutboxAction> OutboxActions { get; }
}

/// <summary>Asynchronous transactional repository for definitions, latches, executions, and outbox state.</summary>
public interface ITriggerRepository
{
    /// <summary>Initializes or recovers storage and returns its verified snapshot.</summary>
    Task<TriggerPersistenceResult<TriggerRepositorySnapshot>> OpenAsync(CancellationToken cancellationToken);

    /// <summary>Reads one immutable verified repository snapshot.</summary>
    Task<TriggerPersistenceResult<TriggerRepositorySnapshot>> ReadSnapshotAsync(
        CancellationToken cancellationToken);

    /// <summary>Atomically replaces all ordered definitions at the expected generation.</summary>
    Task<TriggerPersistenceResult> ReplaceDefinitionsAsync(
        TriggerDefinitionWriteRequest request,
        CancellationToken cancellationToken);

    /// <summary>Atomically imports migrated definitions, latch history, diagnostics, and source identity.</summary>
    Task<TriggerPersistenceResult> TryImportMigrationAsync(
        TriggerMigrationImportRequest request,
        CancellationToken cancellationToken);

    /// <summary>Optimistically commits a non-firing latch transition without creating an execution.</summary>
    Task<TriggerPersistenceResult> TryCommitStateAsync(
        TriggerStateCommitRequest request,
        CancellationToken cancellationToken);

    /// <summary>Atomically commits the proposed latch state, execution, and complete outbox.</summary>
    Task<TriggerPersistenceResult<TriggerExecution>> TryCommitExecutionAsync(
        TriggerExecutionCommitRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reads actions requiring execution or startup reconciliation.</summary>
    Task<TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>>> ReadRecoverableActionsAsync(
        CancellationToken cancellationToken);

    /// <summary>Reads the complete ordered outbox, including terminal actions, for one execution.</summary>
    Task<TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>>> ReadExecutionActionsAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    /// <summary>Reads one durable execution and its process epoch.</summary>
    Task<TriggerPersistenceResult<TriggerExecution>> ReadExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    /// <summary>Reads one durable lifecycle handoff by execution and action identity.</summary>
    Task<TriggerPersistenceResult<TriggerLifecycleHandoff>> ReadLifecycleHandoffAsync(
        Guid executionId,
        int actionIndex,
        CancellationToken cancellationToken);

    /// <summary>Optimistically transitions one durable outbox action.</summary>
    Task<TriggerPersistenceResult<TriggerOutboxAction>> TransitionOutboxAsync(
        TriggerOutboxTransition transition,
        CancellationToken cancellationToken);

    /// <summary>Inserts or optimistically transitions one lifecycle handoff.</summary>
    Task<TriggerPersistenceResult<TriggerLifecycleHandoff>> TransitionLifecycleHandoffAsync(
        TriggerLifecycleHandoffTransition transition,
        CancellationToken cancellationToken);

    /// <summary>Creates and atomically promotes one verified last-known-good database backup.</summary>
    Task<TriggerPersistenceResult> CreateBackupAsync(CancellationToken cancellationToken);
}
