using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Transfers ExitApplication ownership to the App-owned lifetime without awaiting shutdown.</summary>
public sealed class TriggerLifecycleHandoffCoordinator : ITriggerLifecycleHandoff
{
    private static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(25);
    private readonly object _syncLock = new();
    private readonly ITriggerRepository _repository;
    private readonly IApplicationLifetimeRequestSink _requests;
    private readonly TimeProvider _timeProvider;
    private readonly Guid _currentProcessEpoch;
    private readonly Dictionary<TriggerLifecycleHandoffIdentity, LifetimeHandoffSession> _sessions = [];

    /// <summary>Initializes the coordinator for one nonempty current process epoch.</summary>
    public TriggerLifecycleHandoffCoordinator(
        ITriggerRepository repository,
        IApplicationLifetimeRequestSink requests,
        TimeProvider timeProvider,
        Guid currentProcessEpoch)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (currentProcessEpoch == Guid.Empty)
        {
            throw new ArgumentException("Process epoch must be nonempty.", nameof(currentProcessEpoch));
        }

        _currentProcessEpoch = currentProcessEpoch;
    }

    /// <inheritdoc />
    public async Task<TriggerActionProbeResult> ProbeAsync(
        TriggerOutboxAction action,
        CancellationToken cancellationToken)
    {
        ValidateExitAction(action);
        TriggerPersistenceResult<TriggerExecution> executionRead = await _repository.ReadExecutionAsync(
            action.ExecutionId,
            cancellationToken).ConfigureAwait(false);
        if (!executionRead.IsSucceeded || executionRead.Value is not TriggerExecution execution)
        {
            return TriggerActionProbeResult.Unknown(
                executionRead.Diagnostic?.Code ?? "trigger.handoff.execution_unavailable");
        }

        TriggerPersistenceResult<TriggerLifecycleHandoff> handoffRead =
            await _repository.ReadLifecycleHandoffAsync(
                action.ExecutionId,
                action.ActionIndex,
                cancellationToken).ConfigureAwait(false);
        if (execution.ProcessEpoch != _currentProcessEpoch)
        {
            if (handoffRead.Status == TriggerPersistenceStatus.NotFound)
            {
                return TriggerActionProbeResult.Desired();
            }

            if (!handoffRead.IsSucceeded || handoffRead.Value is not TriggerLifecycleHandoff priorHandoff)
            {
                return TriggerActionProbeResult.Unknown(
                    handoffRead.Diagnostic?.Code ?? "trigger.handoff.read_unavailable");
            }

            return await CompletePriorEpochAsync(priorHandoff, cancellationToken).ConfigureAwait(false);
        }

        if (handoffRead.Status == TriggerPersistenceStatus.NotFound)
        {
            return TriggerActionProbeResult.NotDesired();
        }

        if (!handoffRead.IsSucceeded || handoffRead.Value is not TriggerLifecycleHandoff handoff)
        {
            return TriggerActionProbeResult.Unknown(
                handoffRead.Diagnostic?.Code ?? "trigger.handoff.read_unavailable");
        }

        return handoff.State switch
        {
            TriggerLifecycleHandoffState.Succeeded => TriggerActionProbeResult.Desired(),
            TriggerLifecycleHandoffState.Uncertain => TriggerActionProbeResult.Unknown(
                handoff.LastError ?? "trigger.handoff.uncertain"),
            _ => TriggerActionProbeResult.NotDesired(),
        };
    }

    /// <inheritdoc />
    public async Task<TriggerActionApplyResult> HandOffAsync(
        TriggerOutboxAction action,
        CancellationToken cancellationToken)
    {
        ValidateExitAction(action);
        TriggerExecution execution = await ReadExecutionRequiredAsync(
            action.ExecutionId,
            cancellationToken).ConfigureAwait(false);
        if (execution.ProcessEpoch != _currentProcessEpoch)
        {
            return TriggerActionApplyResult.Failed("trigger.handoff.prior_epoch");
        }

        TriggerLifecycleHandoffIdentity identity = new(
            action.ExecutionId,
            action.ActionIndex,
            execution.ProcessEpoch);
        TriggerPersistenceResult<TriggerLifecycleHandoff> inserted =
            await _repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    identity.ExecutionId,
                    identity.ActionIndex,
                    identity.ProcessEpoch,
                    null,
                    TriggerLifecycleHandoffState.HandedOff,
                    _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        if (!inserted.IsSucceeded)
        {
            TriggerLifecycleHandoff existing = await ReadHandoffRequiredAsync(
                identity,
                cancellationToken).ConfigureAwait(false);
            if (existing.State is TriggerLifecycleHandoffState.Failed)
            {
                return TriggerActionApplyResult.Failed(
                    existing.LastError ?? "trigger.handoff.failed");
            }

            if (existing.State is TriggerLifecycleHandoffState.Uncertain)
            {
                return TriggerActionApplyResult.Uncertain(
                    existing.LastError ?? "trigger.handoff.uncertain");
            }

            if (existing.State is TriggerLifecycleHandoffState.Succeeded)
            {
                return TriggerActionApplyResult.HandedOff();
            }
        }

        LifetimeHandoffSession session = GetOrCreateSession(identity);
        _requests.TryRequest(ApplicationLifetimeRequest.Exit("trigger-action", session));
        return TriggerActionApplyResult.HandedOff();
    }

    /// <inheritdoc />
    public async Task AcknowledgeReleaseAsync(
        TriggerLifecycleHandoffIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.ProcessEpoch != _currentProcessEpoch)
        {
            throw new InvalidOperationException("Only the current process epoch can acknowledge producer release.");
        }

        await TransitionAsync(
            identity,
            TriggerLifecycleHandoffState.HandedOff,
            TriggerLifecycleHandoffState.ReleaseAcknowledged,
            null,
            cancellationToken).ConfigureAwait(false);
        GetOrCreateSession(identity).Release();
    }

    /// <inheritdoc />
    public async Task AcknowledgeReleasedExecutionAsync(
        TriggerExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.ProcessEpoch != _currentProcessEpoch)
        {
            return;
        }

        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> read =
            await _repository.ReadExecutionActionsAsync(
                execution.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (!read.IsSucceeded || read.Value is not IReadOnlyList<TriggerOutboxAction> actions)
        {
            throw new InvalidOperationException(
                read.Diagnostic?.Code ?? "trigger.handoff.outbox_unavailable");
        }

        TriggerOutboxAction? handedOffExit = actions.SingleOrDefault(action =>
            action.State == TriggerOutboxState.HandedOff
            && action.DesiredEffect.Kind == TriggerActionKind.ExitApplication);
        if (handedOffExit is null)
        {
            return;
        }

        await AcknowledgeReleaseAsync(
            new TriggerLifecycleHandoffIdentity(
                execution.ExecutionId,
                handedOffExit.ActionIndex,
                execution.ProcessEpoch),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TriggerActionProbeResult> CompletePriorEpochAsync(
        TriggerLifecycleHandoff handoff,
        CancellationToken cancellationToken)
    {
        if (handoff.State == TriggerLifecycleHandoffState.Succeeded)
        {
            return TriggerActionProbeResult.Desired();
        }

        TriggerLifecycleHandoffIdentity identity = new(
            handoff.ExecutionId,
            handoff.ActionIndex,
            handoff.ProcessEpoch);
        await TransitionAsync(
            identity,
            handoff.State,
            TriggerLifecycleHandoffState.Succeeded,
            null,
            cancellationToken,
            recoveryProcessEpoch: _currentProcessEpoch).ConfigureAwait(false);
        return TriggerActionProbeResult.Desired();
    }

    private LifetimeHandoffSession GetOrCreateSession(TriggerLifecycleHandoffIdentity identity)
    {
        lock (_syncLock)
        {
            if (!_sessions.TryGetValue(identity, out LifetimeHandoffSession? session))
            {
                session = new LifetimeHandoffSession(this, identity);
                _sessions.Add(identity, session);
            }

            return session;
        }
    }

    private async Task WaitForReleaseAsync(
        TriggerLifecycleHandoffIdentity identity,
        Task releaseSignal,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (releaseSignal.IsCompleted)
            {
                await releaseSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            TriggerLifecycleHandoff handoff = await ReadHandoffRequiredAsync(
                identity,
                cancellationToken).ConfigureAwait(false);
            if (handoff.ProcessEpoch != identity.ProcessEpoch)
            {
                throw new InvalidDataException("The lifecycle handoff process epoch is inconsistent.");
            }

            if (handoff.State == TriggerLifecycleHandoffState.ReleaseAcknowledged)
            {
                return;
            }

            if (handoff.State != TriggerLifecycleHandoffState.HandedOff)
            {
                throw new InvalidOperationException(
                    $"The lifetime runner observed unexpected handoff state '{handoff.State}' while waiting for release.");
            }

            Task delay = Task.Delay(ReleasePollInterval, cancellationToken);
            Task completed = await Task.WhenAny(releaseSignal, delay).ConfigureAwait(false);
            await completed.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(completed, releaseSignal))
            {
                return;
            }
        }
    }

    private async Task<TriggerExecution> ReadExecutionRequiredAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        TriggerPersistenceResult<TriggerExecution> result = await _repository.ReadExecutionAsync(
            executionId,
            cancellationToken).ConfigureAwait(false);
        return result.IsSucceeded && result.Value is TriggerExecution execution
            ? execution
            : throw new InvalidOperationException(
                result.Diagnostic?.Code ?? "trigger.handoff.execution_unavailable");
    }

    private async Task<TriggerLifecycleHandoff> ReadHandoffRequiredAsync(
        TriggerLifecycleHandoffIdentity identity,
        CancellationToken cancellationToken)
    {
        TriggerPersistenceResult<TriggerLifecycleHandoff> result =
            await _repository.ReadLifecycleHandoffAsync(
                identity.ExecutionId,
                identity.ActionIndex,
                cancellationToken).ConfigureAwait(false);
        return result.IsSucceeded && result.Value is TriggerLifecycleHandoff handoff
            ? handoff
            : throw new InvalidOperationException(
                result.Diagnostic?.Code ?? "trigger.handoff.read_unavailable");
    }

    private async Task TransitionAsync(
        TriggerLifecycleHandoffIdentity identity,
        TriggerLifecycleHandoffState expectedState,
        TriggerLifecycleHandoffState nextState,
        string? diagnosticCode,
        CancellationToken cancellationToken,
        Guid? recoveryProcessEpoch = null)
    {
        TriggerLifecycleHandoff current = await ReadHandoffRequiredAsync(
            identity,
            cancellationToken).ConfigureAwait(false);
        if (current.State == nextState || IsAfter(current.State, nextState))
        {
            return;
        }

        if (current.State != expectedState)
        {
            throw new InvalidOperationException(
                $"Lifecycle handoff expected '{expectedState}' but observed '{current.State}'.");
        }

        TriggerPersistenceResult<TriggerLifecycleHandoff> result =
            await _repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    identity.ExecutionId,
                    identity.ActionIndex,
                    identity.ProcessEpoch,
                    expectedState,
                    nextState,
                    _timeProvider.GetUtcNow(),
                    diagnosticCode,
                    recoveryProcessEpoch),
                cancellationToken).ConfigureAwait(false);
        if (!result.IsSucceeded)
        {
            TriggerLifecycleHandoff reconciled = await ReadHandoffRequiredAsync(
                identity,
                cancellationToken).ConfigureAwait(false);
            if (reconciled.State != nextState && !IsAfter(reconciled.State, nextState))
            {
                throw new InvalidOperationException(
                    result.Diagnostic?.Code ?? "trigger.handoff.transition_unavailable");
            }
        }
    }

    private static bool IsAfter(
        TriggerLifecycleHandoffState current,
        TriggerLifecycleHandoffState requested)
    {
        return requested switch
        {
            TriggerLifecycleHandoffState.ReleaseAcknowledged => current is
                TriggerLifecycleHandoffState.ShutdownStarted or
                TriggerLifecycleHandoffState.Succeeded or
                TriggerLifecycleHandoffState.Failed or
                TriggerLifecycleHandoffState.Uncertain,
            TriggerLifecycleHandoffState.ShutdownStarted => current is
                TriggerLifecycleHandoffState.Succeeded or
                TriggerLifecycleHandoffState.Failed or
                TriggerLifecycleHandoffState.Uncertain,
            _ => false,
        };
    }

    private static void ValidateExitAction(TriggerOutboxAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.DesiredEffect.Kind != TriggerActionKind.ExitApplication
            || action.DesiredEffect.Parameters is not NoActionParameters)
        {
            throw new ArgumentException("Lifecycle handoff requires an ExitApplication action.", nameof(action));
        }
    }

    private sealed class LifetimeHandoffSession(
        TriggerLifecycleHandoffCoordinator owner,
        TriggerLifecycleHandoffIdentity identity) : IApplicationLifetimeHandoff
    {
        private readonly TaskCompletionSource<object?> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string IdempotencyKey => identity.CreateKey();

        public Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
            owner.WaitForReleaseAsync(identity, _released.Task, cancellationToken);

        public Task MarkShutdownStartedAsync(CancellationToken cancellationToken) =>
            owner.TransitionAsync(
                identity,
                TriggerLifecycleHandoffState.ReleaseAcknowledged,
                TriggerLifecycleHandoffState.ShutdownStarted,
                null,
                cancellationToken);

        public Task MarkShutdownSucceededAsync(CancellationToken cancellationToken) =>
            owner.TransitionAsync(
                identity,
                TriggerLifecycleHandoffState.ShutdownStarted,
                TriggerLifecycleHandoffState.Succeeded,
                null,
                cancellationToken);

        public Task MarkShutdownFailedAsync(
            ApplicationLifetimeShutdownFailureKind failureKind,
            string diagnosticCode,
            CancellationToken cancellationToken)
        {
            if (!Enum.IsDefined(failureKind))
            {
                throw new ArgumentOutOfRangeException(nameof(failureKind));
            }

            TriggerLifecycleHandoffState state = failureKind == ApplicationLifetimeShutdownFailureKind.Failed
                ? TriggerLifecycleHandoffState.Failed
                : TriggerLifecycleHandoffState.Uncertain;
            return owner.TransitionAsync(
                identity,
                TriggerLifecycleHandoffState.ShutdownStarted,
                state,
                diagnosticCode,
                cancellationToken);
        }

        public void Release() => _released.TrySetResult(null);
    }
}
