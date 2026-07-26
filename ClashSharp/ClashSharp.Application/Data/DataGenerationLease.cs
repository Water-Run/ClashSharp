namespace ClashSharp.ApplicationModel.Data;

/// <summary>Pins one immutable data-generation scope for the duration of an operation.</summary>
public sealed class DataGenerationLease : IDisposable, IAsyncDisposable
{
    private DataGenerationManager? _owner;
    private readonly DataGenerationScope _scope;

    internal DataGenerationLease(
        DataGenerationManager owner,
        DataGenerationScope scope)
    {
        _owner = owner;
        _scope = scope;
    }

    /// <summary>Gets the descriptor pinned by this lease.</summary>
    public DataGenerationDescriptor Descriptor => _scope.Descriptor;

    internal DataGenerationScope Scope => _scope;

    /// <summary>Releases the pinned operation. Repeated calls have no effect.</summary>
    public void Dispose()
    {
        DataGenerationManager? owner = Interlocked.Exchange(ref _owner, null);
        owner?.ReleaseLease(_scope);
    }

    /// <summary>Releases the pinned operation. Repeated calls have no effect.</summary>
    /// <returns>An already-completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
