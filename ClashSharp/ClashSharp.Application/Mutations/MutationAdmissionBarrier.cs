namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Thrown when a mutation request is not allowed by the current admission state.</summary>
public sealed class MutationAdmissionRejectedException : InvalidOperationException
{
    /// <summary>Initializes a rejection for the observed barrier state.</summary>
    /// <param name="state">Admission state that rejected the request.</param>
    public MutationAdmissionRejectedException(MutationAdmissionState state)
        : base($"Mutation admission is not available while the barrier is '{state}'.")
    {
        State = state;
    }

    /// <summary>Gets the state that rejected the request.</summary>
    public MutationAdmissionState State { get; }
}

/// <summary>Represents one admitted ordinary, destructive, shutdown, or recovery operation.</summary>
public sealed class MutationAdmissionLease : IDisposable, IAsyncDisposable
{
    private MutationAdmissionBarrier? _owner;
    private readonly MutationAdmissionLeaseKind _kind;

    internal MutationAdmissionLease(
        MutationAdmissionBarrier owner,
        MutationAdmissionLeaseKind kind,
        CancellationToken revocationToken = default)
    {
        _owner = owner;
        _kind = kind;
        RevocationToken = revocationToken;
    }

    /// <summary>Gets whether this lease excludes all other admission leases.</summary>
    public bool IsExclusive => _kind != MutationAdmissionLeaseKind.Ordinary;

    /// <summary>Gets the token signaled when closing admission revokes ordinary work waiting for the mutation gate.</summary>
    public CancellationToken RevocationToken { get; }

    internal MutationAdmissionLeaseKind Kind => _kind;

    internal bool IsOwnedBy(MutationAdmissionBarrier barrier)
    {
        return ReferenceEquals(_owner, barrier);
    }

    /// <summary>Completes the sole recovery attempt and atomically chooses open, retained, or shutdown admission.</summary>
    /// <param name="journalPresent">Whether a replay-capable journal remains durable.</param>
    /// <param name="verifiedSuccess">Whether recovery verified its permitted final state.</param>
    public void CompleteRecoveryAttempt(bool journalPresent, bool verifiedSuccess)
    {
        MutationAdmissionBarrier? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
        {
            return;
        }

        owner.CompleteRecoveryAttempt(_kind, journalPresent, verifiedSuccess);
    }

    /// <summary>Releases this lease. Repeated calls have no effect.</summary>
    public void Dispose()
    {
        MutationAdmissionBarrier? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(_kind);
    }

    /// <summary>Releases this lease. Repeated calls have no effect.</summary>
    /// <returns>An already-completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Coordinates ordinary admission, drain, recovery-only work, and terminal shutdown.</summary>
public sealed class MutationAdmissionBarrier
{
    private readonly object _syncLock = new();
    private MutationAdmissionState _state = MutationAdmissionState.Open;
    private CancellationTokenSource _ordinaryRevocationSource = new();
    private int _ordinaryLeaseCount;
    private bool _exclusiveLeaseActive;
    private bool _pendingRecoveryOnly;
    private MutationAdmissionClosure? _pendingClosure;
    private TaskCompletionSource<MutationAdmissionLease>? _drainSignal;
    private TaskCompletionSource<object?>? _recoveryReadySignal;
    private TaskCompletionSource<object?>? _recoveryShutdownSignal;

    /// <summary>Gets the current admission state.</summary>
    public MutationAdmissionState State
    {
        get
        {
            lock (_syncLock)
            {
                return _state;
            }
        }
    }

    /// <summary>Acquires one ordinary admission lease while the barrier is open.</summary>
    /// <param name="cancellationToken">Cancels acquisition before the lease is granted.</param>
    /// <returns>An ordinary lease that must be disposed.</returns>
    public ValueTask<MutationAdmissionLease> AcquireOrdinaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncLock)
        {
            if (_state != MutationAdmissionState.Open || _exclusiveLeaseActive)
            {
                throw new MutationAdmissionRejectedException(_state);
            }

            _ordinaryLeaseCount++;
            return ValueTask.FromResult(new MutationAdmissionLease(
                this,
                MutationAdmissionLeaseKind.Ordinary,
                _ordinaryRevocationSource.Token));
        }
    }

    /// <summary>Closes ordinary admission, revokes gate waiters, and waits for existing leases to drain.</summary>
    /// <param name="closure">Whether admission should reopen or become terminal after drain.</param>
    /// <param name="cancellationToken">Cancels the pending close before its exclusive lease is granted.</param>
    /// <returns>An exclusive lease that must be disposed.</returns>
    public ValueTask<MutationAdmissionLease> CloseAndDrainAsync(
        MutationAdmissionClosure closure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource revocationSource;
        ValueTask<MutationAdmissionLease> result;
        lock (_syncLock)
        {
            if (_state != MutationAdmissionState.Open || _exclusiveLeaseActive || _drainSignal is not null)
            {
                throw new MutationAdmissionRejectedException(_state);
            }

            _state = MutationAdmissionState.Closing;
            _pendingClosure = closure;
            revocationSource = _ordinaryRevocationSource;
            if (_ordinaryLeaseCount == 0)
            {
                result = ValueTask.FromResult(GrantExclusiveUnderLock(closure));
            }
            else
            {
                TaskCompletionSource<MutationAdmissionLease> drainSignal =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                _drainSignal = drainSignal;
                result = new ValueTask<MutationAdmissionLease>(WaitForDrainAsync(drainSignal, cancellationToken));
            }
        }

        revocationSource.Cancel();
        return result;
    }

    /// <summary>Transitions an idle open barrier into recovery-only admission.</summary>
    public void EnterRecoveryOnly()
    {
        CancellationTokenSource revocationSource;
        lock (_syncLock)
        {
            if (_state != MutationAdmissionState.Open || _ordinaryLeaseCount != 0 || _exclusiveLeaseActive)
            {
                throw new MutationAdmissionRejectedException(_state);
            }

            _state = MutationAdmissionState.RecoveryOnly;
            revocationSource = _ordinaryRevocationSource;
        }

        revocationSource.Cancel();
    }

    /// <summary>Acquires the sole recovery lease while the barrier is recovery-only.</summary>
    /// <param name="cancellationToken">Cancels acquisition before the lease is granted.</param>
    /// <returns>An exclusive recovery lease that must be disposed.</returns>
    public ValueTask<MutationAdmissionLease> AcquireRecoveryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncLock)
        {
            if (_state != MutationAdmissionState.RecoveryOnly || _exclusiveLeaseActive)
            {
                throw new MutationAdmissionRejectedException(_state);
            }

            _exclusiveLeaseActive = true;
            return ValueTask.FromResult(new MutationAdmissionLease(this, MutationAdmissionLeaseKind.Recovery));
        }
    }

    /// <summary>Prevents new recovery attempts and waits for an active attempt to reach one durable boundary.</summary>
    /// <param name="cancellationToken">Cancels only this caller's wait; shutdown remains pending.</param>
    /// <returns>A task that completes when admission is terminally closed.</returns>
    public Task RequestRecoveryShutdownAsync(CancellationToken cancellationToken)
    {
        Task shutdownTask;
        lock (_syncLock)
        {
            if (_state == MutationAdmissionState.ClosedForShutdown)
            {
                return Task.CompletedTask;
            }

            if (_state is not (MutationAdmissionState.RecoveryOnly or MutationAdmissionState.RecoveryClosing))
            {
                throw new MutationAdmissionRejectedException(_state);
            }

            if (!_exclusiveLeaseActive)
            {
                _state = MutationAdmissionState.ClosedForShutdown;
                return Task.CompletedTask;
            }

            _state = MutationAdmissionState.RecoveryClosing;
            _recoveryShutdownSignal ??= new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            shutdownTask = _recoveryShutdownSignal.Task;
        }

        return shutdownTask.WaitAsync(cancellationToken);
    }

    internal Task BeginRecoveryOnlyTransition(MutationAdmissionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        CancellationTokenSource revocationSource;
        Task recoveryReady;
        lock (_syncLock)
        {
            if (!lease.IsOwnedBy(this)
                || lease.Kind is not (MutationAdmissionLeaseKind.Ordinary or MutationAdmissionLeaseKind.Destructive))
            {
                throw new InvalidOperationException("Only the current ordinary or destructive lease can retain recovery.");
            }

            bool validState = lease.Kind == MutationAdmissionLeaseKind.Ordinary
                ? _state == MutationAdmissionState.Open
                : _state == MutationAdmissionState.Closing && _exclusiveLeaseActive;
            if (!validState || _pendingRecoveryOnly)
            {
                throw new MutationAdmissionRejectedException(_state);
            }

            _state = MutationAdmissionState.Closing;
            _pendingRecoveryOnly = true;
            _pendingClosure = null;
            _recoveryReadySignal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            recoveryReady = _recoveryReadySignal.Task;
            revocationSource = _ordinaryRevocationSource;
        }

        revocationSource.Cancel();
        return recoveryReady;
    }

    internal void Release(MutationAdmissionLeaseKind kind)
    {
        TaskCompletionSource<MutationAdmissionLease>? drainSignal = null;
        MutationAdmissionLease? exclusiveLease = null;
        TaskCompletionSource<object?>? recoverySignal = null;
        lock (_syncLock)
        {
            if (kind == MutationAdmissionLeaseKind.Ordinary)
            {
                if (_ordinaryLeaseCount <= 0)
                {
                    throw new InvalidOperationException("The ordinary mutation lease count is already zero.");
                }

                _ordinaryLeaseCount--;
                if (_ordinaryLeaseCount == 0)
                {
                    if (_pendingRecoveryOnly)
                    {
                        recoverySignal = EnterRecoveryOnlyUnderLock();
                    }
                    else if (_drainSignal is not null && _pendingClosure is MutationAdmissionClosure closure)
                    {
                        drainSignal = _drainSignal;
                        _drainSignal = null;
                        exclusiveLease = GrantExclusiveUnderLock(closure);
                    }
                }
            }
            else
            {
                if (!_exclusiveLeaseActive)
                {
                    throw new InvalidOperationException("No exclusive mutation admission lease is active.");
                }

                _exclusiveLeaseActive = false;
                if (kind == MutationAdmissionLeaseKind.Destructive && _state == MutationAdmissionState.Closing)
                {
                    if (_pendingRecoveryOnly)
                    {
                        recoverySignal = EnterRecoveryOnlyUnderLock();
                    }
                    else
                    {
                        _pendingClosure = null;
                        ReopenUnderLock();
                    }
                }
            }
        }

        if (drainSignal is not null && exclusiveLease is not null)
        {
            drainSignal.TrySetResult(exclusiveLease);
        }

        recoverySignal?.TrySetResult(null);
    }

    internal void CompleteRecoveryAttempt(
        MutationAdmissionLeaseKind kind,
        bool journalPresent,
        bool verifiedSuccess)
    {
        TaskCompletionSource<object?>? shutdownSignal = null;
        lock (_syncLock)
        {
            if (kind != MutationAdmissionLeaseKind.Recovery
                || !_exclusiveLeaseActive
                || _state is not (MutationAdmissionState.RecoveryOnly or MutationAdmissionState.RecoveryClosing))
            {
                throw new InvalidOperationException("No matching recovery attempt is active.");
            }

            _exclusiveLeaseActive = false;
            if (_state == MutationAdmissionState.RecoveryClosing)
            {
                _state = MutationAdmissionState.ClosedForShutdown;
                shutdownSignal = _recoveryShutdownSignal;
                _recoveryShutdownSignal = null;
            }
            else if (verifiedSuccess && !journalPresent)
            {
                ReopenUnderLock();
            }
            else
            {
                _state = MutationAdmissionState.RecoveryOnly;
            }
        }

        shutdownSignal?.TrySetResult(null);
    }

    private async Task<MutationAdmissionLease> WaitForDrainAsync(
        TaskCompletionSource<MutationAdmissionLease> drainSignal,
        CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                (MutationAdmissionBarrier barrier, TaskCompletionSource<MutationAdmissionLease> signal, CancellationToken token) =
                    ((MutationAdmissionBarrier, TaskCompletionSource<MutationAdmissionLease>, CancellationToken))state!;
                barrier.CancelPendingClose(signal, token);
            },
            (this, drainSignal, cancellationToken));
        return await drainSignal.Task.ConfigureAwait(false);
    }

    private void CancelPendingClose(
        TaskCompletionSource<MutationAdmissionLease> drainSignal,
        CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            if (!ReferenceEquals(_drainSignal, drainSignal))
            {
                return;
            }

            _drainSignal = null;
            _pendingClosure = null;
            ReopenUnderLock();
            drainSignal.TrySetCanceled(cancellationToken);
        }
    }

    private MutationAdmissionLease GrantExclusiveUnderLock(MutationAdmissionClosure closure)
    {
        _exclusiveLeaseActive = true;
        _pendingClosure = closure;
        if (closure == MutationAdmissionClosure.Shutdown)
        {
            _state = MutationAdmissionState.ClosedForShutdown;
            return new MutationAdmissionLease(this, MutationAdmissionLeaseKind.Shutdown);
        }

        return new MutationAdmissionLease(this, MutationAdmissionLeaseKind.Destructive);
    }

    private TaskCompletionSource<object?>? EnterRecoveryOnlyUnderLock()
    {
        _pendingRecoveryOnly = false;
        _pendingClosure = null;
        _state = MutationAdmissionState.RecoveryOnly;
        TaskCompletionSource<object?>? signal = _recoveryReadySignal;
        _recoveryReadySignal = null;
        return signal;
    }

    private void ReopenUnderLock()
    {
        _ordinaryRevocationSource.Dispose();
        _ordinaryRevocationSource = new CancellationTokenSource();
        _state = MutationAdmissionState.Open;
    }
}

internal enum MutationAdmissionLeaseKind
{
    Ordinary,
    Destructive,
    Recovery,
    Shutdown,
}
