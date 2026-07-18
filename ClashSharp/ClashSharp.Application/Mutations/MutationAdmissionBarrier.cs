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

    internal MutationAdmissionLease(MutationAdmissionBarrier owner, MutationAdmissionLeaseKind kind)
    {
        _owner = owner;
        _kind = kind;
    }

    /// <summary>Gets whether this lease excludes all other admission leases.</summary>
    public bool IsExclusive => _kind != MutationAdmissionLeaseKind.Ordinary;

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
    private int _ordinaryLeaseCount;
    private bool _exclusiveLeaseActive;
    private MutationAdmissionClosure? _pendingClosure;
    private TaskCompletionSource<MutationAdmissionLease>? _drainSignal;

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
            return ValueTask.FromResult(new MutationAdmissionLease(this, MutationAdmissionLeaseKind.Ordinary));
        }
    }

    /// <summary>Closes ordinary admission and waits for existing leases to drain.</summary>
    /// <param name="closure">Whether admission should reopen or become terminal after drain.</param>
    /// <param name="cancellationToken">Cancels the pending close before its exclusive lease is granted.</param>
    /// <returns>An exclusive lease that must be disposed.</returns>
    public ValueTask<MutationAdmissionLease> CloseAndDrainAsync(
        MutationAdmissionClosure closure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<MutationAdmissionLease> drainSignal;
        lock (_syncLock)
        {
            if (_state != MutationAdmissionState.Open || _exclusiveLeaseActive || _drainSignal is not null)
            {
                throw new MutationAdmissionRejectedException(_state);
            }

            _state = MutationAdmissionState.Closing;
            _pendingClosure = closure;
            if (_ordinaryLeaseCount == 0)
            {
                return ValueTask.FromResult(GrantExclusiveUnderLock(closure));
            }

            drainSignal = new TaskCompletionSource<MutationAdmissionLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainSignal = drainSignal;
        }

        return new ValueTask<MutationAdmissionLease>(WaitForDrainAsync(drainSignal, cancellationToken));
    }

    /// <summary>Transitions an idle open barrier into recovery-only admission.</summary>
    public void EnterRecoveryOnly()
    {
        lock (_syncLock)
        {
            if (_state != MutationAdmissionState.Open || _ordinaryLeaseCount != 0 || _exclusiveLeaseActive)
            {
                throw new MutationAdmissionRejectedException(_state);
            }

            _state = MutationAdmissionState.RecoveryOnly;
        }
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

    internal void Release(MutationAdmissionLeaseKind kind)
    {
        TaskCompletionSource<MutationAdmissionLease>? signal = null;
        MutationAdmissionLease? exclusiveLease = null;
        lock (_syncLock)
        {
            if (kind == MutationAdmissionLeaseKind.Ordinary)
            {
                if (_ordinaryLeaseCount <= 0)
                {
                    throw new InvalidOperationException("The ordinary mutation lease count is already zero.");
                }

                _ordinaryLeaseCount--;
                if (_ordinaryLeaseCount == 0 && _drainSignal is not null && _pendingClosure is MutationAdmissionClosure closure)
                {
                    signal = _drainSignal;
                    _drainSignal = null;
                    exclusiveLease = GrantExclusiveUnderLock(closure);
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
                    _pendingClosure = null;
                    _state = MutationAdmissionState.Open;
                }
            }
        }

        if (signal is not null && exclusiveLease is not null)
        {
            signal.TrySetResult(exclusiveLease);
        }
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
            _state = MutationAdmissionState.Open;
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
}

internal enum MutationAdmissionLeaseKind
{
    Ordinary,
    Destructive,
    Recovery,
    Shutdown,
}
