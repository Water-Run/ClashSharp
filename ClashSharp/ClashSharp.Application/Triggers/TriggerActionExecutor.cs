using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Processes one committed execution's durable outbox strictly in action order.</summary>
public sealed class TriggerActionExecutor : ITriggerExecutionDispatcher
{
    private readonly ITriggerRepository _repository;
    private readonly ITriggerActionRuntime _runtime;

    /// <summary>Initializes one durable action executor.</summary>
    public TriggerActionExecutor(ITriggerRepository repository, ITriggerActionRuntime runtime)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(
        TriggerExecution execution,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(execution, admissionLease, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconciles and executes ordered actions until success or the first blocking state.</summary>
    public async Task<IReadOnlyList<TriggerActionResult>> ExecuteAsync(
        TriggerExecution execution,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(admissionLease);
        return await ExecuteCoreAsync(
            execution.ExecutionId,
            execution.TaskRevision,
            admissionLease,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<IReadOnlyList<TriggerActionResult>> ReconcileAsync(
        Guid executionId,
        long taskRevision,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("Execution identity must be nonempty.", nameof(executionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(taskRevision);
        ArgumentNullException.ThrowIfNull(admissionLease);
        return ExecuteCoreAsync(executionId, taskRevision, admissionLease, cancellationToken);
    }

    private async Task<IReadOnlyList<TriggerActionResult>> ExecuteCoreAsync(
        Guid executionId,
        long taskRevision,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> read =
            await _repository.ReadExecutionActionsAsync(
                executionId,
                cancellationToken).ConfigureAwait(false);
        if (!read.IsSucceeded || read.Value is not IReadOnlyList<TriggerOutboxAction> actions)
        {
            throw new InvalidOperationException(
                read.Diagnostic?.Code ?? "trigger.outbox.read_unavailable");
        }

        ValidateOrderedActions(executionId, taskRevision, actions);
        List<TriggerActionResult> results = [];
        foreach (TriggerOutboxAction action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.State == TriggerOutboxState.Succeeded)
            {
                continue;
            }

            if (action.State is TriggerOutboxState.Failed or
                TriggerOutboxState.Uncertain or
                TriggerOutboxState.HandedOff)
            {
                results.Add(ToTerminalResult(action));
                break;
            }

            TriggerActionResult result = await ProcessRecoverableAsync(
                action,
                admissionLease,
                cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (result.FinalState != TriggerOutboxState.Succeeded)
            {
                break;
            }
        }

        return results.AsReadOnly();
    }

    private async Task<TriggerActionResult> ProcessRecoverableAsync(
        TriggerOutboxAction action,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        if (action.State == TriggerOutboxState.Running)
        {
            TriggerActionProbeResult recoveryProbe = await _runtime.ProbeAsync(
                action,
                cancellationToken).ConfigureAwait(false);
            if (recoveryProbe.Status == TriggerActionProbeStatus.Desired)
            {
                return await CommitTerminalAsync(
                    action,
                    TriggerOutboxState.Succeeded,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            action = await TransitionRequiredAsync(
                action,
                TriggerOutboxState.Pending,
                recoveryProbe.DiagnosticCode,
                cancellationToken).ConfigureAwait(false);
        }

        TriggerActionProbeResult initialProbe = await _runtime.ProbeAsync(
            action,
            cancellationToken).ConfigureAwait(false);
        action = await TransitionRequiredAsync(
            action,
            TriggerOutboxState.Running,
            null,
            cancellationToken).ConfigureAwait(false);
        if (initialProbe.Status == TriggerActionProbeStatus.Desired)
        {
            return await CommitTerminalAsync(
                action,
                TriggerOutboxState.Succeeded,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        TriggerActionApplyResult applied = await _runtime.ApplyAsync(
            action,
            admissionLease,
            cancellationToken).ConfigureAwait(false);
        return applied.Status switch
        {
            TriggerActionApplyStatus.Failed => await CommitTerminalAsync(
                action,
                TriggerOutboxState.Failed,
                applied.DiagnosticCode,
                cancellationToken).ConfigureAwait(false),
            TriggerActionApplyStatus.Uncertain => await CommitTerminalAsync(
                action,
                TriggerOutboxState.Uncertain,
                applied.DiagnosticCode,
                cancellationToken).ConfigureAwait(false),
            TriggerActionApplyStatus.HandedOff => await CommitTerminalAsync(
                action,
                TriggerOutboxState.HandedOff,
                null,
                cancellationToken).ConfigureAwait(false),
            TriggerActionApplyStatus.Applied => await VerifyAppliedAsync(
                action,
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Undefined trigger action application status."),
        };
    }

    private async Task<TriggerActionResult> VerifyAppliedAsync(
        TriggerOutboxAction action,
        CancellationToken cancellationToken)
    {
        TriggerActionProbeResult verification = await _runtime.ProbeAsync(
            action,
            cancellationToken).ConfigureAwait(false);
        return verification.Status switch
        {
            TriggerActionProbeStatus.Desired => await CommitTerminalAsync(
                action,
                TriggerOutboxState.Succeeded,
                null,
                cancellationToken).ConfigureAwait(false),
            TriggerActionProbeStatus.NotDesired => await CommitTerminalAsync(
                action,
                TriggerOutboxState.Failed,
                "trigger.action.verification_failed",
                cancellationToken).ConfigureAwait(false),
            TriggerActionProbeStatus.Unknown => await CommitTerminalAsync(
                action,
                TriggerOutboxState.Uncertain,
                verification.DiagnosticCode,
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Undefined trigger action probe status."),
        };
    }

    private async Task<TriggerActionResult> CommitTerminalAsync(
        TriggerOutboxAction action,
        TriggerOutboxState terminalState,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        TriggerOutboxAction committed = await TransitionRequiredAsync(
            action,
            terminalState,
            diagnosticCode,
            cancellationToken).ConfigureAwait(false);
        return new TriggerActionResult(committed, terminalState, diagnosticCode);
    }

    private async Task<TriggerOutboxAction> TransitionRequiredAsync(
        TriggerOutboxAction action,
        TriggerOutboxState nextState,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        TriggerPersistenceResult<TriggerOutboxAction> result = await _repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                action.ExecutionId,
                action.ActionIndex,
                action.State,
                nextState,
                diagnosticCode),
            cancellationToken).ConfigureAwait(false);
        return result.IsSucceeded && result.Value is TriggerOutboxAction updated
            ? updated
            : throw new InvalidOperationException(
                result.Diagnostic?.Code ?? "trigger.outbox.transition_conflict");
    }

    private static void ValidateOrderedActions(
        Guid executionId,
        long taskRevision,
        IReadOnlyList<TriggerOutboxAction> actions)
    {
        if (actions.Count == 0
            || actions.Where((action, index) =>
                action.ExecutionId != executionId
                || action.TaskRevision != taskRevision
                || action.ActionIndex != index).Any())
        {
            throw new InvalidDataException("Execution outbox is missing, unordered, or inconsistent.");
        }
    }

    private static TriggerActionResult ToTerminalResult(TriggerOutboxAction action)
    {
        string? diagnosticCode = action.State switch
        {
            TriggerOutboxState.Failed => action.LastError ?? "trigger.action.failed",
            TriggerOutboxState.Uncertain => action.LastError ?? "trigger.action.uncertain",
            _ => null,
        };
        return new TriggerActionResult(action, action.State, diagnosticCode);
    }
}
