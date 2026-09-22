using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Owns queued synchronous UI operations until they finish or are revoked before execution.</summary>
/// <remarks>The containing scope retires this owner before releasing its window and generation.</remarks>
public sealed class OwnedUiDispatcher : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<bool> _hasThreadAccess;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly HashSet<Operation> _operations = [];
    private readonly CancellationTokenRegistration _windowLifetime;
    private bool _closed;
    private Task? _retirement;

    /// <summary>Creates a dispatcher boundary without accessing the platform or scheduling work.</summary>
    /// <param name="hasThreadAccess">Reports access to the owning UI thread.</param>
    /// <param name="tryEnqueue">Queues one callback; rejection never permits a fallback on another thread.</param>
    /// <param name="windowLifetime">Revoked when the window can no longer execute queued work.</param>
    public OwnedUiDispatcher(Func<bool> hasThreadAccess, Func<Action, bool> tryEnqueue, CancellationToken windowLifetime)
    {
        _hasThreadAccess = hasThreadAccess ?? throw new ArgumentNullException(nameof(hasThreadAccess));
        _tryEnqueue = tryEnqueue ?? throw new ArgumentNullException(nameof(tryEnqueue));
        _windowLifetime = windowLifetime.Register(static state => ((OwnedUiDispatcher)state!).Close(), this);
    }

    /// <summary>Waits for the actual callback, including after cancellation or a lost enqueue reply.</summary>
    /// <typeparam name="T">The immutable result captured on the UI thread.</typeparam>
    /// <param name="action">Synchronous work that must not launch unowned asynchronous operations.</param>
    /// <param name="cancellationToken">Cancels before the callback starts effects.</param>
    /// <returns>The callback's result or original failure.</returns>
    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        QueuedOperation<T> operation = new(this, action, cancellationToken);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _operations.Add(operation);
        }
        try
        {
            if (_hasThreadAccess()) { operation.Run(); }
            else if (!_tryEnqueue(operation.Run)) { operation.Reject(new InvalidOperationException("The UI dispatcher rejected the operation.")); }
        }
        catch (Exception exception)
        {
            // A throwing enqueue may already have handed the callback to the queue. Revoke
            // only unstarted work; a callback already executing retains its completion owner.
            operation.Reject(exception);
            if (ExceptionGraphClassifier.IsProcessFatal(exception)) { throw; }
        }
        return operation.Result;
    }

    /// <summary>Rejects new and unstarted work, then drains every callback that has begun execution.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate) { return new(_retirement ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        Task[] pending = Close();
        await _windowLifetime.DisposeAsync().ConfigureAwait(false);
        foreach (Task completion in pending)
        {
            try { await completion.ConfigureAwait(false); }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)) { }
        }
    }

    private Task[] Close()
    {
        Operation[] operations;
        lock (_gate) { _closed = true; operations = _operations.ToArray(); }
        foreach (Operation operation in operations) { operation.Reject(new ObjectDisposedException(nameof(OwnedUiDispatcher))); }
        return operations.Select(operation => operation.Completion).ToArray();
    }

    private void Complete(Operation operation) { lock (_gate) { _operations.Remove(operation); } }

    private abstract class Operation
    {
        public abstract Task Completion { get; }
        public abstract void Reject(Exception exception);
    }

    private sealed class QueuedOperation<T>(OwnedUiDispatcher owner, Func<T> action, CancellationToken cancellationToken) : Operation
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;
        public Task<T> Result => _completion.Task;
        public override Task Completion => _completion.Task;

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) { return; }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _completion.TrySetResult(action());
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
                if (ExceptionGraphClassifier.IsProcessFatal(exception)) { throw; }
            }
            finally { Volatile.Write(ref _state, 2); owner.Complete(this); }
        }

        public override void Reject(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) { return; }
            _completion.TrySetException(exception);
            owner.Complete(this);
        }
    }
}
