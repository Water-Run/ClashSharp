using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.ApplicationModel.Mutations;

internal interface IAdmittedApplicationMutationCoordinator
{
    /// <summary>Executes without reacquiring an ordinary or drained-destructive admission lease.</summary>
    Task<MutationResult<T>> ExecuteAdmittedAsync<T>(
        MutationAdmissionLease admissionLease,
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<MutationPlan>> planFactory,
        Func<MutationContext, CancellationToken, Task<T>> resultFactory,
        CancellationToken cancellationToken);
}

/// <summary>Executes top-level mutations through admission, a fair gate, durable phases, and bounded recovery.</summary>
public sealed class ApplicationMutationCoordinator :
    IApplicationMutationCoordinator,
    IAdmittedApplicationMutationCoordinator
{
    private readonly MutationAdmissionBarrier _admissionBarrier;
    private readonly FairAsyncMutationGate _mutationGate;
    private readonly IMutationJournalStore _journalStore;
    private readonly IMutationRecoveryPlanResolver _recoveryPlanResolver;
    private readonly MutationDeadlines _deadlines;
    private readonly MutationStepRunner _stepRunner;
    private readonly MutationJournalWriter _journalWriter;
    private readonly MutationRecoveryExecutor _recoveryExecutor;

    /// <summary>Initializes the process-wide mutation coordinator.</summary>
    /// <param name="admissionBarrier">Process-wide mutation admission state machine.</param>
    /// <param name="mutationGate">Fair, non-reentrant mutation gate.</param>
    /// <param name="journalStore">Durable single-operation journal store.</param>
    /// <param name="recoveryPlanResolver">Resolver for retained operation plans.</param>
    /// <param name="deadlines">Independent bounded recovery deadlines.</param>
    public ApplicationMutationCoordinator(
        MutationAdmissionBarrier admissionBarrier,
        FairAsyncMutationGate mutationGate,
        IMutationJournalStore journalStore,
        IMutationRecoveryPlanResolver recoveryPlanResolver,
        MutationDeadlines deadlines)
    {
        _admissionBarrier = admissionBarrier ?? throw new ArgumentNullException(nameof(admissionBarrier));
        _mutationGate = mutationGate ?? throw new ArgumentNullException(nameof(mutationGate));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _recoveryPlanResolver = recoveryPlanResolver ?? throw new ArgumentNullException(nameof(recoveryPlanResolver));
        _deadlines = deadlines ?? throw new ArgumentNullException(nameof(deadlines));
        _deadlines.Validate();
        _stepRunner = new MutationStepRunner(_deadlines);
        _journalWriter = new MutationJournalWriter(_journalStore, _stepRunner);
        _recoveryExecutor = new MutationRecoveryExecutor(
            _journalStore,
            _recoveryPlanResolver,
            _journalWriter,
            _stepRunner);
    }

    /// <inheritdoc />
    public Task<MutationResult<T>> ExecuteAsync<T>(
        MutationRequest request,
        MutationPlan plan,
        Func<MutationContext, CancellationToken, Task<T>> resultFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ExecuteAsync(
            request,
            (_, _) => Task.FromResult(plan),
            resultFactory,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MutationResult<T>> ExecuteAsync<T>(
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<MutationPlan>> planFactory,
        Func<MutationContext, CancellationToken, Task<T>> resultFactory,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(planFactory);
        ArgumentNullException.ThrowIfNull(resultFactory);

        MutationAdmissionLease? admissionLease;
        try
        {
            admissionLease = request.RequiresQuiescence
                ? await _admissionBarrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken)
                : await _admissionBarrier.AcquireOrdinaryAsync(cancellationToken);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return CreateResult<T>(request.OperationId, MutationOutcome.Cancelled, default, "mutation-cancelled");
        }

        Task? recoveryReady = null;
        ExecutionEnvelope<T> execution;
        try
        {
            using CancellationTokenSource admittedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                admissionLease.RevocationToken);
            try
            {
                execution = await _mutationGate.ExecuteAsync(
                    request.OperationId,
                    async (context, _) =>
                    {
                        ExecutionEnvelope<T> result;
                        try
                        {
                            result = await ExecutePlannedMutationAsync(
                                request,
                                planFactory,
                                resultFactory,
                                context,
                                admittedCancellation.Token).ConfigureAwait(false);
                        }
                        catch (Exception processFatalFailure) when (
                            ExceptionGraphClassifier.IsProcessFatal(processFatalFailure))
                        {
                            try
                            {
                                recoveryReady = _admissionBarrier.BeginRecoveryOnlyTransition(
                                    admissionLease);
                            }
                            catch (Exception transitionFailure)
                            {
                                throw new AggregateException(
                                    processFatalFailure,
                                    transitionFailure);
                            }

                            throw;
                        }

                        if (result.RetainRecovery)
                        {
                            recoveryReady = _admissionBarrier.BeginRecoveryOnlyTransition(admissionLease);
                        }

                        return result;
                    },
                    admittedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                admittedCancellation.IsCancellationRequested
                && !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                execution = new ExecutionEnvelope<T>(
                    CreateResult<T>(request.OperationId, MutationOutcome.Cancelled, default, "mutation-cancelled"),
                    RetainRecovery: false);
            }

        }
        finally
        {
            await admissionLease.DisposeAsync().ConfigureAwait(false);
        }

        if (recoveryReady is not null)
        {
            await recoveryReady.ConfigureAwait(false);
        }

        return execution.Result;
    }

    async Task<MutationResult<T>> IAdmittedApplicationMutationCoordinator.ExecuteAdmittedAsync<T>(
        MutationAdmissionLease admissionLease,
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<MutationPlan>> planFactory,
        Func<MutationContext, CancellationToken, Task<T>> resultFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admissionLease);
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(planFactory);
        ArgumentNullException.ThrowIfNull(resultFactory);
        bool validAdmission = admissionLease.Kind switch
        {
            MutationAdmissionLeaseKind.Ordinary =>
                _admissionBarrier.State == MutationAdmissionState.Open,
            MutationAdmissionLeaseKind.Destructive =>
                _admissionBarrier.State == MutationAdmissionState.Closing,
            _ => false,
        };
        if (!admissionLease.IsOwnedBy(_admissionBarrier) || !validAdmission)
        {
            throw new InvalidOperationException(
                "An admitted mutation requires an active ordinary or drained destructive admission lease.");
        }

        ExecutionEnvelope<T> execution;
        try
        {
            execution = await _mutationGate.ExecuteAsync(
                request.OperationId,
                async (context, gateToken) =>
                {
                    ExecutionEnvelope<T> result;
                    try
                    {
                        result = await ExecutePlannedMutationAsync(
                            request,
                            planFactory,
                            resultFactory,
                            context,
                            gateToken).ConfigureAwait(false);
                    }
                    catch (Exception processFatalFailure) when (
                        ExceptionGraphClassifier.IsProcessFatal(processFatalFailure))
                    {
                        try
                        {
                            _ = _admissionBarrier.BeginRecoveryOnlyTransition(admissionLease);
                        }
                        catch (Exception transitionFailure)
                        {
                            throw new AggregateException(
                                processFatalFailure,
                                transitionFailure);
                        }

                        throw;
                    }

                    if (result.RetainRecovery)
                    {
                        _ = _admissionBarrier.BeginRecoveryOnlyTransition(admissionLease);
                    }

                    return result;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            execution = new ExecutionEnvelope<T>(
                CreateResult<T>(request.OperationId, MutationOutcome.Cancelled, default, "mutation-cancelled"),
                RetainRecovery: false);
        }

        return execution.Result;
    }

    /// <inheritdoc />
    public void EnsureContextOwnership(MutationContext context)
    {
        _mutationGate.EnsureContextOwnership(context);
    }

    /// <inheritdoc />
    public async Task<MutationResult<object?>> RetryRecoveryAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A recovery operation identifier cannot be empty.", nameof(operationId));
        }

        MutationAdmissionLease recoveryLease;
        try
        {
            recoveryLease = await _admissionBarrier.AcquireRecoveryAsync(cancellationToken);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return CreateResult<object?>(operationId, MutationOutcome.Cancelled, null, "recovery-cancelled");
        }

        MutationRecoveryExecutionResult execution;
        try
        {
            try
            {
                execution = await _mutationGate.ExecuteAsync(
                    operationId,
                    (context, _) => _recoveryExecutor.ExecuteAsync(operationId, context, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                cancellationToken.IsCancellationRequested
                && !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                execution = new MutationRecoveryExecutionResult(
                    CreateResult<object?>(operationId, MutationOutcome.Cancelled, null, "recovery-cancelled"),
                    JournalPresent: true,
                    VerifiedSuccess: false);
            }

            recoveryLease.CompleteRecoveryAttempt(execution.JournalPresent, execution.VerifiedSuccess);
        }
        catch
        {
            recoveryLease.Dispose();
            throw;
        }

        return execution.Result;
    }

    private async Task<ExecutionEnvelope<T>> ExecuteNewMutationAsync<T>(
        MutationRequest request,
        MutationPlan plan,
        Func<MutationContext, CancellationToken, Task<T>> resultFactory,
        MutationContext context,
        CancellationToken callerToken)
    {
        try
        {
            await plan.ValidateAsync(callerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            callerToken.IsCancellationRequested
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return new ExecutionEnvelope<T>(
                CreateResult<T>(request.OperationId, MutationOutcome.Cancelled, default, "mutation-cancelled"),
                RetainRecovery: false);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return new ExecutionEnvelope<T>(
                CreateResult<T>(request.OperationId, MutationOutcome.Failed, default, "mutation-plan-failed"),
                RetainRecovery: false);
        }

        MutationJournalSnapshot snapshot;
        try
        {
            snapshot = await _journalWriter.CreateInitialAsync(request, plan, callerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            callerToken.IsCancellationRequested
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return new ExecutionEnvelope<T>(
                CreateResult<T>(request.OperationId, MutationOutcome.Cancelled, default, "mutation-cancelled"),
                RetainRecovery: false);
        }
        catch (MutationJournalStoreException exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return new ExecutionEnvelope<T>(
                CreateResult<T>(
                    request.OperationId,
                    MutationOutcome.RecoveryRequired,
                    default,
                    ClassifyError(exception, "mutation-journal-create-failed")),
                RetainRecovery: true);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return new ExecutionEnvelope<T>(
                CreateResult<T>(request.OperationId, MutationOutcome.Failed, default, "mutation-journal-create-failed"),
                RetainRecovery: false);
        }

        using CancellationTokenSource recoveryDeadline = _stepRunner.CreateTotalDeadline();
        List<IApplicationMutationParticipant> touchedParticipants = [];
        bool firstSideEffectStarted = false;
        bool committed = false;
        try
        {
            foreach (IApplicationMutationParticipant participant in plan.Participants)
            {
                snapshot = await SaveParticipantBoundaryAsync(
                    snapshot,
                    participant.Name,
                    MutationJournalPhase.Staging,
                    completed: false,
                    callerToken).ConfigureAwait(false);
                touchedParticipants.Add(participant);
                firstSideEffectStarted = true;
                await RunBoundedAsync(
                    token => participant.StageAsync(context, token),
                    recoveryDeadline.Token).ConfigureAwait(false);
                snapshot = await SaveParticipantBoundaryAsync(
                    snapshot,
                    participant.Name,
                    MutationJournalPhase.Staging,
                    completed: true,
                    recoveryDeadline.Token).ConfigureAwait(false);
                callerToken.ThrowIfCancellationRequested();

                snapshot = await SaveParticipantBoundaryAsync(
                    snapshot,
                    participant.Name,
                    MutationJournalPhase.Applying,
                    completed: false,
                    recoveryDeadline.Token).ConfigureAwait(false);
                await RunBoundedAsync(
                    token => participant.ApplyAsync(context, token),
                    recoveryDeadline.Token).ConfigureAwait(false);
                snapshot = await SaveParticipantBoundaryAsync(
                    snapshot,
                    participant.Name,
                    MutationJournalPhase.Applying,
                    completed: true,
                    recoveryDeadline.Token).ConfigureAwait(false);
                callerToken.ThrowIfCancellationRequested();

                snapshot = await SaveParticipantBoundaryAsync(
                    snapshot,
                    participant.Name,
                    MutationJournalPhase.Verifying,
                    completed: false,
                    recoveryDeadline.Token).ConfigureAwait(false);
                await RunBoundedAsync(
                    token => participant.VerifyAsync(context, token),
                    recoveryDeadline.Token).ConfigureAwait(false);
                snapshot = await SaveParticipantBoundaryAsync(
                    snapshot,
                    participant.Name,
                    MutationJournalPhase.Verifying,
                    completed: true,
                    recoveryDeadline.Token).ConfigureAwait(false);
                callerToken.ThrowIfCancellationRequested();
            }

            snapshot = await SaveOperationBoundaryAsync(
                snapshot,
                MutationJournalPhase.TargetPromoted,
                completed: false,
                hasCommitMarker: false,
                recoveryDeadline.Token).ConfigureAwait(false);
            await RunBoundedAsync(
                token => plan.PromoteTargetAsync(context, token),
                recoveryDeadline.Token).ConfigureAwait(false);
            snapshot = await SaveOperationBoundaryAsync(
                snapshot,
                MutationJournalPhase.TargetPromoted,
                completed: true,
                hasCommitMarker: false,
                recoveryDeadline.Token).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();

            snapshot = await SaveOperationBoundaryAsync(
                snapshot,
                MutationJournalPhase.Verifying,
                completed: false,
                hasCommitMarker: false,
                recoveryDeadline.Token).ConfigureAwait(false);
            await RunBoundedAsync(
                token => plan.VerifyDesiredTargetAsync(context, token),
                recoveryDeadline.Token).ConfigureAwait(false);
            snapshot = await SaveOperationBoundaryAsync(
                snapshot,
                MutationJournalPhase.Verifying,
                completed: true,
                hasCommitMarker: false,
                recoveryDeadline.Token).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();

            snapshot = await SaveOperationBoundaryAsync(
                snapshot,
                MutationJournalPhase.Committed,
                completed: true,
                hasCommitMarker: true,
                recoveryDeadline.Token).ConfigureAwait(false);
            committed = true;

            snapshot = await _recoveryExecutor.CompleteForwardAsync(
                plan,
                context,
                snapshot,
                recoveryDeadline.Token).ConfigureAwait(false);
            T value = await RunBoundedAsync(
                token => resultFactory(context, token),
                recoveryDeadline.Token).ConfigureAwait(false);
            await RunBoundedAsync(
                token => _journalStore.DeleteAsync(request.OperationId, snapshot.ContentHash, token),
                recoveryDeadline.Token).ConfigureAwait(false);
            return new ExecutionEnvelope<T>(
                CreateResult(request.OperationId, MutationOutcome.Succeeded, value, null),
                RetainRecovery: false);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            if (committed)
            {
                return new ExecutionEnvelope<T>(
                    CreateResult<T>(
                        request.OperationId,
                        MutationOutcome.CommittedRecoveryRequired,
                        default,
                        ClassifyError(exception, "mutation-committed-recovery-required")),
                    RetainRecovery: true);
            }

            return await CompensateFailedMutationAsync<T>(
                request.OperationId,
                plan,
                context,
                snapshot,
                touchedParticipants,
                firstSideEffectStarted,
                exception,
                recoveryDeadline.Token).ConfigureAwait(false);
        }
    }

    private async Task<ExecutionEnvelope<T>> ExecutePlannedMutationAsync<T>(
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<MutationPlan>> planFactory,
        Func<MutationContext, CancellationToken, Task<T>> resultFactory,
        MutationContext context,
        CancellationToken callerToken)
    {
        MutationPlan plan;
        try
        {
            plan = await planFactory(context, callerToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The mutation plan factory returned null.");
        }
        catch (OperationCanceledException exception) when (
            callerToken.IsCancellationRequested
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return new ExecutionEnvelope<T>(
                CreateResult<T>(request.OperationId, MutationOutcome.Cancelled, default, "mutation-cancelled"),
                RetainRecovery: false);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return new ExecutionEnvelope<T>(
                CreateResult<T>(request.OperationId, MutationOutcome.Failed, default, "mutation-plan-failed"),
                RetainRecovery: false);
        }

        return await ExecuteNewMutationAsync(request, plan, resultFactory, context, callerToken).ConfigureAwait(false);
    }

    private async Task<ExecutionEnvelope<T>> CompensateFailedMutationAsync<T>(
        Guid operationId,
        MutationPlan plan,
        MutationContext context,
        MutationJournalSnapshot snapshot,
        IReadOnlyList<IApplicationMutationParticipant> touchedParticipants,
        bool firstSideEffectStarted,
        Exception failure,
        CancellationToken recoveryToken)
    {
        if (!firstSideEffectStarted)
        {
            try
            {
                await RunBoundedAsync(
                    token => _journalStore.DeleteAsync(operationId, snapshot.ContentHash, token),
                    recoveryToken).ConfigureAwait(false);
                MutationOutcome outcome = failure is OperationCanceledException
                    ? MutationOutcome.Cancelled
                    : MutationOutcome.Failed;
                return new ExecutionEnvelope<T>(
                    CreateResult<T>(operationId, outcome, default, ClassifyError(failure, "mutation-failed")),
                    RetainRecovery: false);
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                return new ExecutionEnvelope<T>(
                    CreateResult<T>(operationId, MutationOutcome.RecoveryRequired, default, "mutation-journal-cleanup-failed"),
                    RetainRecovery: true);
            }
        }

        try
        {
            foreach (IApplicationMutationParticipant participant in touchedParticipants.Reverse())
            {
                snapshot = await SaveParticipantBoundaryAsync(
                    snapshot,
                    participant.Name,
                    MutationJournalPhase.Compensating,
                    completed: false,
                    recoveryToken).ConfigureAwait(false);
                await RunBoundedAsync(
                    token => participant.CompensateAsync(context, token),
                    recoveryToken).ConfigureAwait(false);
                MutationProbeState state = await RunBoundedAsync(
                    token => participant.ProbeAsync(context, token),
                    recoveryToken).ConfigureAwait(false);
                if (state != MutationProbeState.Baseline)
                {
                    throw new InvalidOperationException($"Participant '{participant.Name}' did not verify its baseline after compensation.");
                }

                snapshot = await SaveParticipantBoundaryAsync(
                    snapshot,
                    participant.Name,
                    MutationJournalPhase.Compensating,
                    completed: true,
                    recoveryToken).ConfigureAwait(false);
            }

            snapshot = await SaveOperationBoundaryAsync(
                snapshot,
                MutationJournalPhase.Compensating,
                completed: false,
                hasCommitMarker: false,
                recoveryToken).ConfigureAwait(false);
            await RunBoundedAsync(
                token => plan.RestoreBaselineAsync(context, token),
                recoveryToken).ConfigureAwait(false);
            await RunBoundedAsync(
                token => plan.VerifyBaselineAsync(context, token),
                recoveryToken).ConfigureAwait(false);
            snapshot = await SaveOperationBoundaryAsync(
                snapshot,
                MutationJournalPhase.Compensating,
                completed: true,
                hasCommitMarker: false,
                recoveryToken).ConfigureAwait(false);
            await RunBoundedAsync(
                token => _journalStore.DeleteAsync(operationId, snapshot.ContentHash, token),
                recoveryToken).ConfigureAwait(false);
            return new ExecutionEnvelope<T>(
                CreateResult<T>(operationId, MutationOutcome.Compensated, default, ClassifyError(failure, "mutation-compensated")),
                RetainRecovery: false);
        }
        catch (Exception compensationException) when (
            !ExceptionGraphClassifier.IsProcessFatal(compensationException))
        {
            return new ExecutionEnvelope<T>(
                CreateResult<T>(
                    operationId,
                    MutationOutcome.RecoveryRequired,
                    default,
                    ClassifyError(compensationException, "mutation-recovery-required")),
                RetainRecovery: true);
        }
    }

    private Task<MutationJournalSnapshot> SaveParticipantBoundaryAsync(
        MutationJournalSnapshot snapshot,
        string participantName,
        MutationJournalPhase phase,
        bool completed,
        CancellationToken cancellationToken)
    {
        return _journalWriter.SaveParticipantBoundaryAsync(
            snapshot,
            participantName,
            phase,
            completed,
            cancellationToken);
    }

    private Task<MutationJournalSnapshot> SaveOperationBoundaryAsync(
        MutationJournalSnapshot snapshot,
        MutationJournalPhase phase,
        bool completed,
        bool hasCommitMarker,
        CancellationToken cancellationToken)
    {
        return _journalWriter.SaveOperationBoundaryAsync(
            snapshot,
            phase,
            completed,
            hasCommitMarker,
            cancellationToken);
    }

    private Task RunBoundedAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken totalRecoveryToken)
    {
        return _stepRunner.RunAsync(operation, totalRecoveryToken);
    }

    private Task<T> RunBoundedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken totalRecoveryToken)
    {
        return _stepRunner.RunAsync(operation, totalRecoveryToken);
    }

    private static void ValidateRequest(MutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("A mutation operation identifier cannot be empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.OperationType))
        {
            throw new ArgumentException("A mutation operation type cannot be empty.", nameof(request));
        }
    }

    private static string ClassifyError(Exception? exception, string fallback)
    {
        return exception switch
        {
            OperationCanceledException => $"{fallback}-cancelled",
            TimeoutException => $"{fallback}-timeout",
            _ => fallback,
        };
    }

    private static MutationResult<T> CreateResult<T>(
        Guid operationId,
        MutationOutcome outcome,
        T? value,
        string? errorCode)
    {
        return new MutationResult<T>(operationId, outcome, value, errorCode);
    }

    private sealed record ExecutionEnvelope<T>(MutationResult<T> Result, bool RetainRecovery);
}
