using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Identifies the durable outcome of coordinating one task evaluation.</summary>
public enum TriggerEvaluationStatus
{
    /// <summary>The task no longer exists in the current repository snapshot.</summary>
    NotFound = 0,

    /// <summary>The current task definition is disabled and requested no context.</summary>
    Disabled = 1,

    /// <summary>The predicate did not match; any latch transition was persisted.</summary>
    NotMatched = 2,

    /// <summary>Unavailable context prevented a sound state transition.</summary>
    ContextUnavailable = 3,

    /// <summary>The latch, execution, and complete action outbox committed atomically.</summary>
    Committed = 4,

    /// <summary>Repeated optimistic conflicts prevented a current decision.</summary>
    Conflict = 5,

    /// <summary>The repository could not provide or commit an authoritative result.</summary>
    RepositoryUnavailable = 6,
}

/// <summary>Identifies whether a committed execution was offered to its durable action dispatcher.</summary>
public enum TriggerDispatchStatus
{
    /// <summary>No execution committed, so dispatch was not requested.</summary>
    NotRequested = 0,

    /// <summary>The dispatcher completed its current durable processing pass.</summary>
    Completed = 1,

    /// <summary>Admission, cancellation, or a contained dispatcher failure left durable work recoverable.</summary>
    Deferred = 2,
}

/// <summary>Dispatches one committed execution while holding its ordinary mutation admission lease.</summary>
public interface ITriggerExecutionDispatcher
{
    /// <summary>
    /// Processes only durable outbox actions and uses the supplied admission lease for mutations
    /// instead of reacquiring ordinary admission.
    /// </summary>
    /// <param name="execution">Execution already committed with its complete outbox.</param>
    /// <param name="admissionLease">Ordinary admission lease held for the processing pass.</param>
    /// <param name="cancellationToken">Cancels work revoked before mutation-gate entry.</param>
    Task DispatchAsync(
        TriggerExecution execution,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken);
}

/// <summary>Immutable durable result of one serialized task evaluation.</summary>
public sealed class TriggerEvaluationResult
{
    internal TriggerEvaluationResult(
        string taskId,
        TriggerEvaluationStatus status,
        TriggerExecution? execution,
        TriggerDispatchStatus dispatchStatus,
        string? diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!Enum.IsDefined(dispatchStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchStatus));
        }

        bool validShape = status == TriggerEvaluationStatus.Committed
            ? execution is not null && dispatchStatus != TriggerDispatchStatus.NotRequested
            : execution is null && dispatchStatus == TriggerDispatchStatus.NotRequested;
        if (!validShape)
        {
            throw new ArgumentException("Execution and dispatch status do not match the evaluation status.");
        }

        TaskId = taskId;
        Status = status;
        Execution = execution;
        DispatchStatus = dispatchStatus;
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the stable task identity requested by the caller.</summary>
    public string TaskId { get; }

    /// <summary>Gets the typed coordination outcome.</summary>
    public TriggerEvaluationStatus Status { get; }

    /// <summary>Gets the committed execution when the outcome is committed.</summary>
    public TriggerExecution? Execution { get; }

    /// <summary>Gets whether the committed execution reached its action dispatcher.</summary>
    public TriggerDispatchStatus DispatchStatus { get; }

    /// <summary>Gets an optional stable diagnostic code.</summary>
    public string? DiagnosticCode { get; }
}

/// <summary>Serializes, reloads, evaluates, and transactionally commits one trigger task.</summary>
public sealed class TriggerExecutionCoordinator
{
    private const int DefaultConflictAttempts = 3;
    private readonly ITriggerRepository _repository;
    private readonly TriggerExecutionGate _executionGate;
    private readonly TriggerEvaluator _evaluator;
    private readonly MutationAdmissionBarrier _admissionBarrier;
    private readonly ITriggerExecutionDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly Guid _processEpoch;
    private readonly Func<Guid> _createExecutionId;
    private readonly int _conflictAttempts;

    /// <summary>Initializes one trigger evaluation coordinator.</summary>
    public TriggerExecutionCoordinator(
        ITriggerRepository repository,
        TriggerExecutionGate executionGate,
        TriggerEvaluator evaluator,
        MutationAdmissionBarrier admissionBarrier,
        ITriggerExecutionDispatcher dispatcher,
        TimeProvider timeProvider,
        Guid processEpoch,
        Func<Guid>? createExecutionId = null,
        int conflictAttempts = DefaultConflictAttempts)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _executionGate = executionGate ?? throw new ArgumentNullException(nameof(executionGate));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _admissionBarrier = admissionBarrier ?? throw new ArgumentNullException(nameof(admissionBarrier));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (processEpoch == Guid.Empty)
        {
            throw new ArgumentException("Process epoch must be nonempty.", nameof(processEpoch));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(conflictAttempts);
        _processEpoch = processEpoch;
        _createExecutionId = createExecutionId ?? Guid.NewGuid;
        _conflictAttempts = conflictAttempts;
    }

    /// <summary>Evaluates one task under its keyed gate and commits only an authoritative transition.</summary>
    public async Task<TriggerEvaluationResult> EvaluateAsync(
        string taskId,
        TriggerEventKind eventKind,
        TriggerNotificationLevel? notificationLevel,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (!Enum.IsDefined(eventKind))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        if (notificationLevel is TriggerNotificationLevel level && !Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(notificationLevel));
        }

        await using TriggerExecutionLease executionLease = await _executionGate.EnterAsync(
            taskId,
            cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < _conflictAttempts; attempt++)
        {
            TriggerPersistenceResult<TriggerRepositorySnapshot> read =
                await _repository.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!read.IsSucceeded || read.Value is not TriggerRepositorySnapshot snapshot)
            {
                return Result(
                    taskId,
                    TriggerEvaluationStatus.RepositoryUnavailable,
                    diagnosticCode: read.Diagnostic?.Code ?? "trigger.repository.read_unavailable");
            }

            TriggerTaskRecord? task = snapshot.Tasks.FirstOrDefault(
                record => StringComparer.Ordinal.Equals(record.Definition.Id, taskId));
            if (task is null)
            {
                return Result(taskId, TriggerEvaluationStatus.NotFound);
            }

            if (!task.Definition.IsEnabled)
            {
                return Result(taskId, TriggerEvaluationStatus.Disabled);
            }

            TriggerEvaluationDecision evaluation = await _evaluator.EvaluateAsync(
                task,
                eventKind,
                notificationLevel,
                cancellationToken).ConfigureAwait(false);
            TriggerMatchDecision? decision = evaluation.MatchDecision;
            if (decision is null
                || decision.Outcome == TriggerMatchOutcome.InsufficientData
                || evaluation.ContextResult.Status == TriggerContextStatus.Unsound)
            {
                return Result(
                    taskId,
                    TriggerEvaluationStatus.ContextUnavailable,
                    diagnosticCode: evaluation.ContextResult.DiagnosticCode
                        ?? "trigger.context.unsound_decision");
            }

            TriggerPersistenceStatus persistenceStatus;
            if (decision.Outcome == TriggerMatchOutcome.NotMatched)
            {
                if (!HasStateTransition(task.State, decision.NextState))
                {
                    return Result(taskId, TriggerEvaluationStatus.NotMatched);
                }

                TriggerPersistenceResult committedState = await _repository.TryCommitStateAsync(
                    new TriggerStateCommitRequest(
                        task.Definition,
                        decision.ExpectedStateVersion,
                        decision.NextState),
                    cancellationToken).ConfigureAwait(false);
                persistenceStatus = committedState.Status;
                if (committedState.IsSucceeded)
                {
                    return Result(taskId, TriggerEvaluationStatus.NotMatched);
                }

                if (persistenceStatus is not (
                    TriggerPersistenceStatus.Conflict or TriggerPersistenceStatus.NotFound))
                {
                    return Result(
                        taskId,
                        TriggerEvaluationStatus.RepositoryUnavailable,
                        diagnosticCode: committedState.Diagnostic?.Code
                            ?? "trigger.repository.state_commit_unavailable");
                }
            }
            else
            {
                Guid executionId = _createExecutionId();
                if (executionId == Guid.Empty)
                {
                    throw new InvalidOperationException("Execution identity factory returned an empty value.");
                }

                TriggerPersistenceResult<TriggerExecution> committedExecution =
                    await _repository.TryCommitExecutionAsync(
                        new TriggerExecutionCommitRequest(
                            executionId,
                            task.Definition,
                            decision.ExpectedStateVersion,
                            decision.NextState,
                            _timeProvider.GetUtcNow(),
                            _processEpoch),
                        cancellationToken).ConfigureAwait(false);
                persistenceStatus = committedExecution.Status;
                if (committedExecution.IsSucceeded
                    && committedExecution.Value is TriggerExecution execution)
                {
                    return await DispatchCommittedAsync(
                        taskId,
                        execution,
                        cancellationToken).ConfigureAwait(false);
                }

                if (persistenceStatus is not (
                    TriggerPersistenceStatus.Conflict or TriggerPersistenceStatus.NotFound))
                {
                    return Result(
                        taskId,
                        TriggerEvaluationStatus.RepositoryUnavailable,
                        diagnosticCode: committedExecution.Diagnostic?.Code
                            ?? "trigger.repository.execution_commit_unavailable");
                }
            }
        }

        return Result(
            taskId,
            TriggerEvaluationStatus.Conflict,
            diagnosticCode: "trigger.evaluation.conflict_exhausted");
    }

    private async Task<TriggerEvaluationResult> DispatchCommittedAsync(
        string taskId,
        TriggerExecution execution,
        CancellationToken cancellationToken)
    {
        MutationAdmissionLease admissionLease;
        try
        {
            admissionLease = await _admissionBarrier.AcquireOrdinaryAsync(
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                taskId,
                TriggerEvaluationStatus.Committed,
                execution,
                TriggerDispatchStatus.Deferred,
                "trigger.dispatch.cancelled_before_admission");
        }
        catch (MutationAdmissionRejectedException)
        {
            return Result(
                taskId,
                TriggerEvaluationStatus.Committed,
                execution,
                TriggerDispatchStatus.Deferred,
                "trigger.dispatch.admission_closed");
        }

        await using (admissionLease.ConfigureAwait(false))
        using (CancellationTokenSource dispatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            admissionLease.RevocationToken))
        {
            try
            {
                await _dispatcher.DispatchAsync(
                    execution,
                    admissionLease,
                    dispatchCancellation.Token).ConfigureAwait(false);
                return Result(
                    taskId,
                    TriggerEvaluationStatus.Committed,
                    execution,
                    TriggerDispatchStatus.Completed);
            }
            catch (OperationCanceledException) when (dispatchCancellation.IsCancellationRequested)
            {
                return Result(
                    taskId,
                    TriggerEvaluationStatus.Committed,
                    execution,
                    TriggerDispatchStatus.Deferred,
                    "trigger.dispatch.cancelled");
            }
            catch (Exception)
            {
                return Result(
                    taskId,
                    TriggerEvaluationStatus.Committed,
                    execution,
                    TriggerDispatchStatus.Deferred,
                    "trigger.dispatch.failed");
            }
        }
    }

    private static bool HasStateTransition(
        TriggerTaskState current,
        TriggerTaskState proposed)
    {
        return current.TaskRevision != proposed.TaskRevision
            || current.LastTriggeredAt != proposed.LastTriggeredAt
            || current.ConditionStates.Count != proposed.ConditionStates.Count
            || current.ConditionStates.Any(pair =>
                !proposed.ConditionStates.TryGetValue(pair.Key, out TriggerConditionState? next)
                || pair.Value != next);
    }

    private static TriggerEvaluationResult Result(
        string taskId,
        TriggerEvaluationStatus status,
        TriggerExecution? execution = null,
        TriggerDispatchStatus dispatchStatus = TriggerDispatchStatus.NotRequested,
        string? diagnosticCode = null)
    {
        return new TriggerEvaluationResult(
            taskId,
            status,
            execution,
            dispatchStatus,
            diagnosticCode);
    }
}
