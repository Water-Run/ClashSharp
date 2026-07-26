namespace ClashSharp.ApplicationModel.Data;

/// <summary>Owns current-generation admission, drain, swap, rollback, and scope disposal.</summary>
public sealed partial class DataGenerationManager : IAsyncDisposable
{
    private readonly object _syncLock = new();
    private ManagerState _state;
    private DataGenerationManifestSnapshot? _currentManifest;
    private DataGenerationScope? _currentScope;
    private DataGenerationScope[]? _shutdownScopes;
    private DataGenerationTransition? _transition;
    private TaskCompletionSource<DataGenerationTransition>? _drainCompletion;
    private TaskCompletionSource<object?>? _disposalCompletion;
    private TaskCompletionSource<object?>? _leaseDrainCompletion;
    private TaskCompletionSource<object?>? _transitionOperationCompletion;
    private TaskCompletionSource<object?>? _storeOperationCompletion;
    private DataGenerationTransition? _storeOperationTransition;
    private object? _storeOperationToken;
    private int _leaseCount;
    private bool _transitionCompletionInProgress;

    /// <summary>Gets the verified manifest currently represented by the in-memory facade.</summary>
    public DataGenerationManifestSnapshot CurrentManifest
    {
        get
        {
            lock (_syncLock)
            {
                if (_state is ManagerState.Disposing
                    or ManagerState.DisposalFailed
                    or ManagerState.Disposed)
                {
                    throw CreateStateException();
                }

                return _currentManifest ?? throw CreateStateException();
            }
        }
    }

    /// <summary>Initializes the first verified active scope exactly once.</summary>
    /// <param name="manifest">Verified durable current-generation manifest.</param>
    /// <param name="scope">Paused scope constructed for that exact descriptor.</param>
    public void Initialize(
        DataGenerationManifestSnapshot manifest,
        DataGenerationScope scope)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(scope);
        lock (_syncLock)
        {
            if (_state != ManagerState.Uninitialized)
            {
                throw CreateStateException();
            }

            if (!scope.Descriptor.IsSameGeneration(manifest.Descriptor)
                || !scope.TryActivate(this))
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidStage,
                    "The initial scope does not match the verified current manifest.");
            }

            _currentManifest = manifest;
            _currentScope = scope;
            _state = ManagerState.Active;
        }
    }

    /// <summary>Pins the current immutable scope for one ordinary operation.</summary>
    /// <param name="cancellationToken">Cancels acquisition before a lease is granted.</param>
    /// <returns>A lease that must cover the complete repository operation.</returns>
    public ValueTask<DataGenerationLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncLock)
        {
            if (_state != ManagerState.Active || _currentScope is null)
            {
                throw CreateStateException();
            }

            if (_currentScope.State != DataGenerationScopeState.Active)
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "The current generation scope is not active.");
            }

            checked
            {
                _leaseCount++;
            }

            return ValueTask.FromResult(new DataGenerationLease(this, _currentScope));
        }
    }

    /// <summary>Rejects later leases and waits for every existing generation operation to finish.</summary>
    /// <param name="expectedCurrentHash">Hash of the manifest the caller intends to replace.</param>
    /// <param name="cancellationToken">Cancels the pending drain before exclusive ownership is granted.</param>
    /// <returns>An exclusive transition that must be explicitly committed, rolled back, or aborted.</returns>
    public ValueTask<DataGenerationTransition> BeginDrainAsync(
        string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        if (!DataGenerationManifestSnapshot.IsCanonicalContentHash(expectedCurrentHash))
        {
            throw new ArgumentException(
                "The expected manifest hash must be canonical lowercase SHA-256 text.",
                nameof(expectedCurrentHash));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncLock)
        {
            if (_state != ManagerState.Active
                || _currentManifest is null
                || _currentScope is null)
            {
                throw CreateStateException();
            }

            if (!string.Equals(
                    expectedCurrentHash,
                    _currentManifest.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.StaleGeneration,
                    "The in-memory generation changed before drain began.");
            }

            _currentScope.BeginDrain(this);
            _state = ManagerState.Draining;
            DataGenerationTransition transition = new(
                this,
                _currentManifest,
                _currentScope);
            _transition = transition;
            if (_leaseCount == 0)
            {
                _state = ManagerState.Transitioning;
                return ValueTask.FromResult(transition);
            }

            TaskCompletionSource<DataGenerationTransition> drainCompletion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainCompletion = drainCompletion;
            return new ValueTask<DataGenerationTransition>(
                WaitForDrainAsync(transition, drainCompletion, cancellationToken));
        }
    }

    internal void ReleaseLease(DataGenerationScope scope)
    {
        TaskCompletionSource<DataGenerationTransition>? drainCompletion = null;
        DataGenerationTransition? transition = null;
        TaskCompletionSource<object?>? disposalDrain = null;
        lock (_syncLock)
        {
            if (_leaseCount <= 0 || !ReferenceEquals(scope, _currentScope))
            {
                throw new InvalidOperationException("The generation lease is not owned by this manager.");
            }

            _leaseCount--;
            if (_leaseCount == 0)
            {
                if (_state == ManagerState.Draining
                    && _drainCompletion is not null
                    && _transition is not null)
                {
                    _state = ManagerState.Transitioning;
                    drainCompletion = _drainCompletion;
                    _drainCompletion = null;
                    transition = _transition;
                }
                else if (_state == ManagerState.Disposing)
                {
                    disposalDrain = _leaseDrainCompletion;
                    _leaseDrainCompletion = null;
                }
            }
        }

        if (drainCompletion is not null && transition is not null)
        {
            drainCompletion.TrySetResult(transition);
        }

        disposalDrain?.TrySetResult(null);
    }

    private async Task<DataGenerationTransition> WaitForDrainAsync(
        DataGenerationTransition transition,
        TaskCompletionSource<DataGenerationTransition> completion,
        CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                (DataGenerationManager manager,
                    DataGenerationTransition transition,
                    TaskCompletionSource<DataGenerationTransition> completion,
                    CancellationToken token) =
                    ((DataGenerationManager,
                        DataGenerationTransition,
                        TaskCompletionSource<DataGenerationTransition>,
                        CancellationToken))state!;
                manager.CancelPendingDrain(transition, completion, token);
            },
            (this, transition, completion, cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    private void CancelPendingDrain(
        DataGenerationTransition transition,
        TaskCompletionSource<DataGenerationTransition> completion,
        CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            if (_state != ManagerState.Draining
                || !ReferenceEquals(_transition, transition)
                || !ReferenceEquals(_drainCompletion, completion))
            {
                return;
            }

            _drainCompletion = null;
            _transition = null;
            _currentScope!.RestoreActive(this);
            _state = ManagerState.Active;
            transition.DetachOwner();
            completion.TrySetCanceled(cancellationToken);
        }
    }

    private DataGenerationManagerException CreateStateException()
    {
        return _state switch
        {
            ManagerState.Uninitialized => new DataGenerationManagerException(
                DataGenerationManagerError.NotInitialized,
                "No verified data generation is initialized."),
            ManagerState.Disposing
                or ManagerState.DisposalFailed
                or ManagerState.Disposed => new DataGenerationManagerException(
                DataGenerationManagerError.Disposed,
                "The generation manager is disposing or disposed."),
            _ => new DataGenerationManagerException(
                DataGenerationManagerError.Draining,
                "The current data generation is draining."),
        };
    }

    private enum ManagerState
    {
        Uninitialized,
        Active,
        Draining,
        Transitioning,
        Disposing,
        DisposalFailed,
        Disposed,
    }
}
