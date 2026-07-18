namespace ClashSharp.ApplicationModel.Mutations;

internal sealed class MutationRecoveryExecutor(
    IMutationJournalStore journalStore,
    IMutationRecoveryPlanResolver recoveryPlanResolver,
    MutationJournalWriter journalWriter,
    MutationStepRunner stepRunner)
{
    public async Task<MutationRecoveryExecutionResult> ExecuteAsync(
        Guid operationId,
        MutationContext context,
        CancellationToken callerToken)
    {
        MutationJournalSnapshot? snapshot;
        try
        {
            snapshot = await journalStore.LoadAsync(callerToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Retained(operationId, exception, "recovery-journal-load-failed");
        }

        if (snapshot is null)
        {
            return new MutationRecoveryExecutionResult(
                CreateResult(operationId, MutationOutcome.Succeeded, null),
                JournalPresent: false,
                VerifiedSuccess: true);
        }

        if (snapshot.Journal.OperationId != operationId)
        {
            return Retained(operationId, null, "recovery-operation-mismatch");
        }

        MutationPlan plan;
        try
        {
            plan = await recoveryPlanResolver.ResolveAsync(snapshot.Journal, callerToken).ConfigureAwait(false);
            ValidateRecoveryPlan(snapshot.Journal, plan);
        }
        catch (Exception exception)
        {
            return Retained(operationId, exception, "recovery-plan-failed");
        }

        RecoveryDirection direction = snapshot.Journal.HasCommitMarker
            ? RecoveryDirection.CompleteForward
            : RecoveryDirection.RestoreBaseline;
        RecoveryHandle handle = new(operationId, snapshot.Journal.Generation, snapshot.ContentHash, direction);
        using CancellationTokenSource recoveryDeadline = stepRunner.CreateTotalDeadline();
        try
        {
            MutationOutcome outcome;
            if (direction == RecoveryDirection.CompleteForward)
            {
                callerToken.ThrowIfCancellationRequested();
                snapshot = await CompleteForwardAsync(plan, context, snapshot, recoveryDeadline.Token).ConfigureAwait(false);
                outcome = MutationOutcome.Succeeded;
            }
            else
            {
                snapshot = await RecoverBaselineAsync(
                    plan,
                    context,
                    snapshot,
                    handle,
                    callerToken,
                    recoveryDeadline.Token).ConfigureAwait(false);
                outcome = MutationOutcome.Compensated;
            }

            await stepRunner.RunAsync(
                token => journalStore.DeleteAsync(operationId, snapshot.ContentHash, token),
                recoveryDeadline.Token).ConfigureAwait(false);
            return new MutationRecoveryExecutionResult(
                CreateResult(operationId, outcome, null),
                JournalPresent: false,
                VerifiedSuccess: true);
        }
        catch (Exception exception)
        {
            return Retained(operationId, exception, "mutation-recovery-required");
        }
    }

    public async Task<MutationJournalSnapshot> CompleteForwardAsync(
        MutationPlan plan,
        MutationContext context,
        MutationJournalSnapshot snapshot,
        CancellationToken recoveryToken)
    {
        snapshot = await journalWriter.SaveOperationBoundaryAsync(
            snapshot,
            MutationJournalPhase.CleaningUp,
            completed: false,
            hasCommitMarker: true,
            recoveryToken).ConfigureAwait(false);
        foreach (IApplicationMutationParticipant participant in plan.Participants)
        {
            await stepRunner.RunAsync(
                token => participant.ActivateAsync(context, token),
                recoveryToken).ConfigureAwait(false);
            await stepRunner.RunAsync(
                token => participant.VerifyAsync(context, token),
                recoveryToken).ConfigureAwait(false);
        }

        await stepRunner.RunAsync(
            token => plan.VerifyDesiredTargetAsync(context, token),
            recoveryToken).ConfigureAwait(false);
        snapshot = await journalWriter.SaveOperationBoundaryAsync(
            snapshot,
            MutationJournalPhase.CleaningUp,
            completed: true,
            hasCommitMarker: true,
            recoveryToken).ConfigureAwait(false);
        snapshot = await journalWriter.SaveOperationBoundaryAsync(
            snapshot,
            MutationJournalPhase.CleaningUp,
            completed: false,
            hasCommitMarker: true,
            recoveryToken).ConfigureAwait(false);
        foreach (IApplicationMutationParticipant participant in plan.Participants)
        {
            await stepRunner.RunAsync(
                token => participant.CleanupAsync(context, token),
                recoveryToken).ConfigureAwait(false);
        }

        return await journalWriter.SaveOperationBoundaryAsync(
            snapshot,
            MutationJournalPhase.CleaningUp,
            completed: true,
            hasCommitMarker: true,
            recoveryToken).ConfigureAwait(false);
    }

    private async Task<MutationJournalSnapshot> RecoverBaselineAsync(
        MutationPlan plan,
        MutationContext context,
        MutationJournalSnapshot snapshot,
        RecoveryHandle handle,
        CancellationToken callerToken,
        CancellationToken recoveryToken)
    {
        Dictionary<string, IApplicationMutationParticipant> participants = plan.Participants
            .ToDictionary(participant => participant.Name, StringComparer.Ordinal);
        bool firstRecoverySideEffect = false;
        foreach (MutationJournalStep step in snapshot.Journal.Steps.Reverse())
        {
            if (!step.IntentRecorded)
            {
                continue;
            }

            if (!participants.TryGetValue(step.Name, out IApplicationMutationParticipant? participant))
            {
                throw new InvalidOperationException($"Recovery participant '{step.Name}' is unavailable.");
            }

            CancellationToken probeToken = firstRecoverySideEffect ? recoveryToken : callerToken;
            MutationProbeState state = await stepRunner.RunAsync(
                token => participant.ProbeAsync(context, token),
                probeToken).ConfigureAwait(false);
            if (state == MutationProbeState.Baseline)
            {
                continue;
            }

            if (state == MutationProbeState.Unknown)
            {
                throw new InvalidOperationException($"Recovery participant '{step.Name}' is in an unknown state.");
            }

            callerToken.ThrowIfCancellationRequested();
            snapshot = await journalWriter.SaveParticipantBoundaryAsync(
                snapshot,
                participant.Name,
                MutationJournalPhase.Compensating,
                completed: false,
                callerToken).ConfigureAwait(false);
            handle.Advance(snapshot);
            firstRecoverySideEffect = true;
            await stepRunner.RunAsync(
                token => participant.CompensateAsync(context, token),
                recoveryToken).ConfigureAwait(false);
            state = await stepRunner.RunAsync(
                token => participant.ProbeAsync(context, token),
                recoveryToken).ConfigureAwait(false);
            if (state != MutationProbeState.Baseline)
            {
                throw new InvalidOperationException($"Recovery participant '{step.Name}' did not verify its baseline.");
            }

            snapshot = await journalWriter.SaveParticipantBoundaryAsync(
                snapshot,
                participant.Name,
                MutationJournalPhase.Compensating,
                completed: true,
                recoveryToken).ConfigureAwait(false);
            handle.Advance(snapshot);
        }

        CancellationToken baselineToken = firstRecoverySideEffect ? recoveryToken : callerToken;
        snapshot = await journalWriter.SaveOperationBoundaryAsync(
            snapshot,
            MutationJournalPhase.Compensating,
            completed: false,
            hasCommitMarker: false,
            baselineToken).ConfigureAwait(false);
        handle.Advance(snapshot);
        if (!firstRecoverySideEffect)
        {
            callerToken.ThrowIfCancellationRequested();
        }

        await stepRunner.RunAsync(token => plan.RestoreBaselineAsync(context, token), recoveryToken).ConfigureAwait(false);
        await stepRunner.RunAsync(token => plan.VerifyBaselineAsync(context, token), recoveryToken).ConfigureAwait(false);
        snapshot = await journalWriter.SaveOperationBoundaryAsync(
            snapshot,
            MutationJournalPhase.Compensating,
            completed: true,
            hasCommitMarker: false,
            recoveryToken).ConfigureAwait(false);
        handle.Advance(snapshot);
        return snapshot;
    }

    private static void ValidateRecoveryPlan(MutationJournal journal, MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(journal.BaselineHash, plan.BaselineHash, StringComparison.Ordinal)
            || !string.Equals(journal.DesiredHash, plan.DesiredHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The resolved recovery plan does not match the journal state hashes.");
        }

        Dictionary<string, string?> journalParticipants = journal.Steps
            .ToDictionary(step => step.Name, step => step.CompensationData, StringComparer.Ordinal);
        if (journalParticipants.Count != plan.Participants.Count)
        {
            throw new InvalidOperationException("The resolved recovery plan participant set does not match the journal.");
        }

        foreach (IApplicationMutationParticipant participant in plan.Participants)
        {
            if (!journalParticipants.TryGetValue(participant.Name, out string? compensationData)
                || !string.Equals(compensationData, participant.CompensationData, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Recovery participant '{participant.Name}' does not match its journal compensation identity.");
            }
        }
    }

    private static MutationRecoveryExecutionResult Retained(
        Guid operationId,
        Exception? exception,
        string fallbackError)
    {
        string errorCode = exception is OperationCanceledException
            ? $"{fallbackError}-cancelled"
            : fallbackError;
        MutationOutcome outcome = exception is OperationCanceledException
            ? MutationOutcome.Cancelled
            : MutationOutcome.RecoveryRequired;
        return new MutationRecoveryExecutionResult(
            CreateResult(operationId, outcome, errorCode),
            JournalPresent: true,
            VerifiedSuccess: false);
    }

    private static MutationResult<object?> CreateResult(
        Guid operationId,
        MutationOutcome outcome,
        string? errorCode)
    {
        return new MutationResult<object?>(operationId, outcome, null, errorCode);
    }
}

internal sealed record MutationRecoveryExecutionResult(
    MutationResult<object?> Result,
    bool JournalPresent,
    bool VerifiedSuccess);
