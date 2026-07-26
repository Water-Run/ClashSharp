namespace ClashSharp.ApplicationModel.Data;

/// <summary>Owns the lifetime boundary for repositories attached to one immutable generation.</summary>
public sealed class DataGenerationScope : IAsyncDisposable
{
    private static readonly object PublicDisposalOwner = new();
    private readonly object _syncLock = new();
    private readonly IAsyncDisposable? _ownedLifetime;
    private TaskCompletionSource<object?>? _disposalCompletion;
    private object? _ownerToken;
    private DataGenerationScopeState _state = DataGenerationScopeState.Staged;

    /// <summary>Initializes a paused scope without starting work or touching the filesystem.</summary>
    /// <param name="descriptor">Immutable generation descriptor.</param>
    /// <param name="ownedLifetime">Optional composite repository lifetime transferred to this scope.</param>
    public DataGenerationScope(
        DataGenerationDescriptor descriptor,
        IAsyncDisposable? ownedLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        _ownedLifetime = ownedLifetime;
    }

    /// <summary>Gets the immutable generation descriptor.</summary>
    public DataGenerationDescriptor Descriptor { get; }

    /// <summary>Gets the current scope lifecycle state.</summary>
    public DataGenerationScopeState State
    {
        get
        {
            lock (_syncLock)
            {
                return _state;
            }
        }
    }

    /// <summary>Disposes an unclaimed staged scope; claimed scopes remain owner-controlled.</summary>
    public ValueTask DisposeAsync()
    {
        return DisposeCoreAsync(PublicDisposalOwner, claimUnownedScope: true);
    }

    internal ValueTask DisposeOwnedAsync(object ownerToken)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        return DisposeCoreAsync(ownerToken, claimUnownedScope: false);
    }

    internal void TransferOwnershipForDisposal(
        object nextOwner,
        object? alternateCurrentOwner)
    {
        ArgumentNullException.ThrowIfNull(nextOwner);
        lock (_syncLock)
        {
            if (_state == DataGenerationScopeState.Disposed)
            {
                return;
            }

            if (!ReferenceEquals(_ownerToken, nextOwner)
                && !ReferenceEquals(_ownerToken, alternateCurrentOwner))
            {
                throw new InvalidOperationException(
                    "The generation scope is not owned by the manager or its active transition.");
            }

            if (_state == DataGenerationScopeState.Disposing)
            {
                throw new InvalidOperationException(
                    "A generation scope cannot transfer ownership while disposal is in progress.");
            }

            _ownerToken = nextOwner;
        }
    }

    private async ValueTask DisposeCoreAsync(
        object ownerToken,
        bool claimUnownedScope)
    {
        TaskCompletionSource<object?> completion;
        bool ownsDisposal;
        lock (_syncLock)
        {
            if (_state == DataGenerationScopeState.Disposed)
            {
                return;
            }

            if (claimUnownedScope)
            {
                if (_ownerToken is null && _state == DataGenerationScopeState.Staged)
                {
                    _ownerToken = PublicDisposalOwner;
                    _state = DataGenerationScopeState.Retired;
                }
                else if (!ReferenceEquals(_ownerToken, PublicDisposalOwner))
                {
                    throw new InvalidOperationException(
                        "A claimed generation scope can only be disposed by its owner.");
                }
            }
            else
            {
                ValidateOwner(ownerToken);
            }

            ownsDisposal = _disposalCompletion is null
                || _state == DataGenerationScopeState.DisposalFailed;
            if (ownsDisposal)
            {
                _disposalCompletion =
                    new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            completion = _disposalCompletion!;
            if (ownsDisposal)
            {
                _state = DataGenerationScopeState.Disposing;
            }
        }

        if (ownsDisposal)
        {
            await CompleteDisposalAsync(completion).ConfigureAwait(false);
        }

        await completion.Task.ConfigureAwait(false);
    }

    internal bool TryActivate(object ownerToken)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        lock (_syncLock)
        {
            if (_state != DataGenerationScopeState.Staged || _ownerToken is not null)
            {
                return false;
            }

            _ownerToken = ownerToken;
            _state = DataGenerationScopeState.Active;
            return true;
        }
    }

    internal bool TryClaimStaged(object ownerToken)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        lock (_syncLock)
        {
            if (_state != DataGenerationScopeState.Staged || _ownerToken is not null)
            {
                return false;
            }

            _ownerToken = ownerToken;
            return true;
        }
    }

    internal void ActivateClaimed(object ownerToken)
    {
        TransitionState(
            ownerToken,
            DataGenerationScopeState.Staged,
            DataGenerationScopeState.Active);
    }

    internal void BeginDrain(object ownerToken)
    {
        TransitionState(
            ownerToken,
            DataGenerationScopeState.Active,
            DataGenerationScopeState.Draining);
    }

    internal void RestoreActive(object ownerToken)
    {
        TransitionState(
            ownerToken,
            DataGenerationScopeState.Draining,
            DataGenerationScopeState.Active);
    }

    internal void Retire(object ownerToken)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        lock (_syncLock)
        {
            ValidateOwner(ownerToken);
            if (_state is not (DataGenerationScopeState.Staged
                or DataGenerationScopeState.Active
                or DataGenerationScopeState.Draining))
            {
                throw new InvalidOperationException(
                    $"A generation scope in state '{_state}' cannot be retired.");
            }

            _state = DataGenerationScopeState.Retired;
        }
    }

    internal void TransferOwnership(object currentOwner, object nextOwner)
    {
        ArgumentNullException.ThrowIfNull(currentOwner);
        ArgumentNullException.ThrowIfNull(nextOwner);
        lock (_syncLock)
        {
            ValidateOwner(currentOwner);
            if (_state != DataGenerationScopeState.Active)
            {
                throw new InvalidOperationException(
                    $"A generation scope in state '{_state}' cannot transfer ownership.");
            }

            _ownerToken = nextOwner;
        }
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource<object?> completion)
    {
        try
        {
            if (_ownedLifetime is not null)
            {
                await _ownedLifetime.DisposeAsync().ConfigureAwait(false);
            }

            lock (_syncLock)
            {
                _state = DataGenerationScopeState.Disposed;
                _ownerToken = null;
            }

            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            lock (_syncLock)
            {
                _state = DataGenerationScopeState.DisposalFailed;
            }

            completion.TrySetException(exception);
        }
    }

    private void TransitionState(
        object ownerToken,
        DataGenerationScopeState expected,
        DataGenerationScopeState target)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        lock (_syncLock)
        {
            ValidateOwner(ownerToken);
            if (_state != expected)
            {
                throw new InvalidOperationException(
                    $"A generation scope in state '{_state}' cannot transition to '{target}'.");
            }

            _state = target;
        }
    }

    private void ValidateOwner(object ownerToken)
    {
        if (!ReferenceEquals(_ownerToken, ownerToken))
        {
            throw new InvalidOperationException(
                "The caller does not own this generation scope.");
        }
    }
}
