namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Represents exclusive asynchronous ownership of one trigger task identity.</summary>
public sealed class TriggerExecutionLease : IDisposable, IAsyncDisposable
{
    private TriggerExecutionGate? _owner;
    private readonly string _taskId;
    private readonly TriggerExecutionGate.GateEntry _entry;

    internal TriggerExecutionLease(
        TriggerExecutionGate owner,
        string taskId,
        TriggerExecutionGate.GateEntry entry)
    {
        _owner = owner;
        _taskId = taskId;
        _entry = entry;
    }

    /// <summary>Releases this task identity. Repeated calls have no effect.</summary>
    public void Dispose()
    {
        TriggerExecutionGate? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(_taskId, _entry);
    }

    /// <summary>Releases this task identity. Repeated calls have no effect.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Serializes evaluation and execution per task while allowing unrelated tasks to progress.</summary>
public sealed class TriggerExecutionGate
{
    private readonly object _syncLock = new();
    private readonly Dictionary<string, GateEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Waits for exclusive ownership of one nonempty task identity.</summary>
    /// <param name="taskId">Stable trigger task identity.</param>
    /// <param name="cancellationToken">Cancels only this pending acquisition.</param>
    /// <returns>A lease that must be disposed after task work completes.</returns>
    public async ValueTask<TriggerExecutionLease> EnterAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        cancellationToken.ThrowIfCancellationRequested();
        GateEntry entry;
        lock (_syncLock)
        {
            if (!_entries.TryGetValue(taskId, out entry!))
            {
                entry = new GateEntry();
                _entries.Add(taskId, entry);
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new TriggerExecutionLease(this, taskId, entry);
        }
        catch
        {
            RemoveReference(taskId, entry);
            throw;
        }
    }

    internal void Release(string taskId, GateEntry entry)
    {
        entry.Semaphore.Release();
        RemoveReference(taskId, entry);
    }

    private void RemoveReference(string taskId, GateEntry entry)
    {
        lock (_syncLock)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0
                && _entries.TryGetValue(taskId, out GateEntry? current)
                && ReferenceEquals(current, entry))
            {
                _entries.Remove(taskId);
                entry.Semaphore.Dispose();
            }
        }
    }

    internal sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }
}
