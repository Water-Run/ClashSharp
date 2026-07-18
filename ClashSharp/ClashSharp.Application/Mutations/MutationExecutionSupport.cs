namespace ClashSharp.ApplicationModel.Mutations;

internal sealed class MutationStepRunner(MutationDeadlines deadlines)
{
    public CancellationTokenSource CreateTotalDeadline()
    {
        return new CancellationTokenSource(deadlines.TotalRecoveryTimeout);
    }

    public async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken totalRecoveryToken)
    {
        using CancellationTokenSource stepDeadline = CancellationTokenSource.CreateLinkedTokenSource(totalRecoveryToken);
        stepDeadline.CancelAfter(deadlines.StepTimeout);
        try
        {
            await operation(stepDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            stepDeadline.IsCancellationRequested && !totalRecoveryToken.IsCancellationRequested)
        {
            throw new TimeoutException("The mutation recovery step exceeded its bounded deadline.", exception);
        }
    }

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken totalRecoveryToken)
    {
        using CancellationTokenSource stepDeadline = CancellationTokenSource.CreateLinkedTokenSource(totalRecoveryToken);
        stepDeadline.CancelAfter(deadlines.StepTimeout);
        try
        {
            return await operation(stepDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            stepDeadline.IsCancellationRequested && !totalRecoveryToken.IsCancellationRequested)
        {
            throw new TimeoutException("The mutation recovery step exceeded its bounded deadline.", exception);
        }
    }
}

internal sealed class MutationJournalWriter(
    IMutationJournalStore journalStore,
    MutationStepRunner stepRunner)
{
    public Task<MutationJournalSnapshot> CreateInitialAsync(
        MutationRequest request,
        MutationPlan plan,
        CancellationToken cancellationToken)
    {
        MutationJournalStep[] steps = plan.Participants
            .Select(participant => new MutationJournalStep(
                participant.Name,
                MutationJournalPhase.Planned,
                IntentRecorded: false,
                Completed: false,
                participant.CompensationData))
            .ToArray();
        MutationJournal journal = new(
            MutationJournal.CurrentSchemaVersion,
            request.OperationId,
            request.OperationType,
            Generation: 1,
            MutationJournalPhase.Planned,
            plan.BaselineHash,
            plan.DesiredHash,
            HasCommitMarker: false,
            steps,
            PhaseIntentRecorded: true,
            PhaseCompleted: true);
        return journalStore.SaveAsync(journal, null, cancellationToken);
    }

    public Task<MutationJournalSnapshot> SaveParticipantBoundaryAsync(
        MutationJournalSnapshot snapshot,
        string participantName,
        MutationJournalPhase phase,
        bool completed,
        CancellationToken cancellationToken)
    {
        MutationJournal journal = snapshot.Journal;
        MutationJournalStep[] steps = journal.Steps
            .Select(step => step.Name == participantName
                ? step with
                {
                    Phase = phase,
                    IntentRecorded = true,
                    Completed = completed,
                }
                : step)
            .ToArray();
        MutationJournal next = journal with
        {
            Generation = journal.Generation + 1,
            Phase = phase,
            Steps = steps,
            PhaseIntentRecorded = true,
            PhaseCompleted = completed,
        };
        return stepRunner.RunAsync(
            token => journalStore.SaveAsync(next, snapshot.ContentHash, token),
            cancellationToken);
    }

    public Task<MutationJournalSnapshot> SaveOperationBoundaryAsync(
        MutationJournalSnapshot snapshot,
        MutationJournalPhase phase,
        bool completed,
        bool hasCommitMarker,
        CancellationToken cancellationToken)
    {
        MutationJournal journal = snapshot.Journal;
        MutationJournal next = journal with
        {
            Generation = journal.Generation + 1,
            Phase = phase,
            HasCommitMarker = hasCommitMarker,
            PhaseIntentRecorded = true,
            PhaseCompleted = completed,
        };
        return stepRunner.RunAsync(
            token => journalStore.SaveAsync(next, snapshot.ContentHash, token),
            cancellationToken);
    }
}
