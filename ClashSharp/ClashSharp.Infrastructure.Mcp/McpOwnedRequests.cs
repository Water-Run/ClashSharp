using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Infrastructure.Agent;

/// <summary>Retains HTTP and tool-handler work even when transport cancellation stops waiting for a reply.</summary>
internal sealed class McpOwnedRequests : IAsyncDisposable
{
    private readonly RepositoryOperationLifetime _operations;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private Task? _retirement;
    private int _count;

    public McpOwnedRequests() => _operations = new(this);

    public Lease? TryEnter()
    {
        IDisposable operation;
        try { operation = _operations.Enter(); }
        catch (ObjectDisposedException) { return null; }
        if (Interlocked.Increment(ref _count) > 64)
        {
            Interlocked.Decrement(ref _count);
            operation.Dispose();
            return null;
        }
        return new(this, operation, _stopping.Token);
    }

    public Task CloseAndDrainAsync()
    {
        lock (_gate) { return _retirement ??= RetireAsync(); }
    }

    public ValueTask DisposeAsync() => new(CloseAndDrainAsync());

    private async Task RetireAsync()
    {
        Task drained = _operations.DisposeAsync().AsTask();
        try { await Task.WhenAll(_stopping.CancelAsync(), drained).ConfigureAwait(false); }
        finally { _stopping.Dispose(); }
    }

    internal sealed class Lease(McpOwnedRequests owner, IDisposable operation, CancellationToken stopping) : IDisposable
    {
        private IDisposable? _operation = operation;
        public CancellationToken Stopping { get; } = stopping;

        public void Dispose()
        {
            IDisposable? active = Interlocked.Exchange(ref _operation, null);
            if (active is null) { return; }
            Interlocked.Decrement(ref owner._count);
            active.Dispose();
        }
    }
}
