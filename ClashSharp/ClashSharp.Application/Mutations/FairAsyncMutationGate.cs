namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Thrown when one logical asynchronous flow tries to recursively enter the mutation gate.</summary>
public sealed class MutationReentrancyException : InvalidOperationException
{
    /// <summary>Initializes a reentrancy failure for the operation that already owns the gate.</summary>
    /// <param name="ownerOperationId">Identifier of the operation that owns the current logical flow.</param>
    public MutationReentrancyException(Guid ownerOperationId)
        : base($"Mutation operation '{ownerOperationId}' cannot recursively acquire the mutation gate.")
    {
        OwnerOperationId = ownerOperationId;
    }

    /// <summary>Gets the operation that already owns the current logical flow.</summary>
    public Guid OwnerOperationId { get; }
}

/// <summary>Serializes top-level mutations through an explicit fair asynchronous queue.</summary>
public sealed class FairAsyncMutationGate
{
    private readonly object _syncLock = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private readonly AsyncLocal<Guid?> _logicalOwner = new();
    private readonly object _ownershipToken = new();
    private bool _isHeld;

    /// <summary>Gets whether one operation currently owns the gate.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_syncLock)
            {
                return _isHeld;
            }
        }
    }

    /// <summary>Gets the number of operations waiting behind the current owner.</summary>
    public int QueuedCount
    {
        get
        {
            lock (_syncLock)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>Runs one non-result mutation callback under the fair gate.</summary>
    /// <param name="operationId">Stable non-empty operation identifier.</param>
    /// <param name="operation">Callback that receives the gate-owned context.</param>
    /// <param name="cancellationToken">Cancels waiting or pre-side-effect callback work.</param>
    /// <returns>A task that completes after the callback releases the gate.</returns>
    public Task ExecuteAsync(
        Guid operationId,
        Func<MutationContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(
            operationId,
            async (context, token) =>
            {
                await operation(context, token).ConfigureAwait(false);
                return null;
            },
            cancellationToken);
    }

    /// <summary>Runs one result-producing mutation callback under the fair gate.</summary>
    /// <typeparam name="T">Type returned by the callback.</typeparam>
    /// <param name="operationId">Stable non-empty operation identifier.</param>
    /// <param name="operation">Callback that receives the gate-owned context.</param>
    /// <param name="cancellationToken">Cancels waiting or pre-side-effect callback work.</param>
    /// <returns>The callback result after the gate is released.</returns>
    public async Task<T> ExecuteAsync<T>(
        Guid operationId,
        Func<MutationContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A mutation operation identifier cannot be empty.", nameof(operationId));
        }

        if (_logicalOwner.Value is Guid ownerOperationId)
        {
            throw new MutationReentrancyException(ownerOperationId);
        }

        await AcquireAsync(cancellationToken).ConfigureAwait(false);
        Guid? previousOwner = _logicalOwner.Value;
        _logicalOwner.Value = operationId;
        MutationContext? context = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            context = new MutationContext(operationId, _ownershipToken);
            return await operation(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            context?.Invalidate();
            _logicalOwner.Value = previousOwner;
            Release();
        }
    }

    internal void EnsureContextOwnership(MutationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureOwnedBy(_ownershipToken);
    }

    private Task AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_syncLock)
        {
            if (!_isHeld && _waiters.Count == 0)
            {
                _isHeld = true;
                return Task.CompletedTask;
            }

            waiter = new Waiter(cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
        }

        return WaitForTurnAsync(waiter, cancellationToken);
    }

    private async Task WaitForTurnAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                (FairAsyncMutationGate gate, Waiter pendingWaiter) = ((FairAsyncMutationGate, Waiter))state!;
                gate.CancelWaiter(pendingWaiter);
            },
            (this, waiter));
        await waiter.Signal.Task.ConfigureAwait(false);
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_syncLock)
        {
            if (waiter.Node?.List is null)
            {
                return;
            }

            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Signal.TrySetCanceled(waiter.CancellationToken);
        }
    }

    private void Release()
    {
        lock (_syncLock)
        {
            while (_waiters.First is LinkedListNode<Waiter> node)
            {
                Waiter waiter = node.Value;
                _waiters.RemoveFirst();
                waiter.Node = null;
                if (waiter.Signal.TrySetResult(null))
                {
                    return;
                }
            }

            _isHeld = false;
        }
    }

    private sealed class Waiter(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource<object?> Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }
    }
}
