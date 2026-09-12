namespace ClashSharp.ApplicationModel.Data;

/// <summary>Retires a repository only after every accepted operation has completed its full storage work.</summary>
public sealed class RepositoryOperationLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _repository;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _retired;

    /// <summary>Creates a lifetime without accessing storage or accepting any operations.</summary>
    /// <param name="repository">Repository identified by a rejection after retirement.</param>
    public RepositoryOperationLifetime(object repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>Accepts one operation whose lease must span all asynchronous work and compensation.</summary>
    /// <returns>An idempotent lease released when the complete operation leaves the repository.</returns>
    public IDisposable Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, _repository);
            _operations++;
            return new Operation(this);
        }
    }

    /// <summary>Rejects new operations immediately and asynchronously waits for accepted operations to drain.</summary>
    /// <returns>The shared completion of repository retirement.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _retired = true;
            if (_operations == 0)
            {
                _drained.TrySetResult();
            }

            return new ValueTask(_drained.Task);
        }
    }

    private void Leave()
    {
        lock (_gate)
        {
            _operations--;
            if (_retired && _operations == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    private sealed class Operation(RepositoryOperationLifetime owner) : IDisposable
    {
        private RepositoryOperationLifetime? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }
}
