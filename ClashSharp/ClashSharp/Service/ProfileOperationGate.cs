using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Service;

/// <summary>Serializes operations for one profile or subscription key without blocking other keys.</summary>
/// <remarks>
/// Invariants: An entry remains registered while a holder or waiter references it.
/// Thread safety: Safe for concurrent callers.
/// Side effects: Waits asynchronously and releases keyed semaphore entries after their last user exits.
/// </remarks>
internal sealed class ProfileOperationGate
{
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);

    /// <summary>Gets a diagnostic snapshot of keys still retained by holders or waiters.</summary>
    internal int ActiveKeyCount => _entries.Count;

    /// <summary>Asynchronously acquires the operation lease for <paramref name="key"/>.</summary>
    /// <remarks>Each gate instance owns a separate key space; callers supply canonical identifiers.</remarks>
    public async ValueTask<IDisposable> EnterAsync(
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry = AcquireEntryReference(key);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseEntryReference(key, entry);
            throw;
        }
    }

    private Entry AcquireEntryReference(string key)
    {
        while (true)
        {
            if (_entries.TryGetValue(key, out Entry? existing))
            {
                if (existing.TryAddReference())
                {
                    return existing;
                }

                continue;
            }

            Entry created = new();
            if (_entries.TryAdd(key, created))
            {
                return created;
            }

            created.Dispose();
        }
    }

    private void Release(string key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseEntryReference(key, entry);
    }

    private void ReleaseEntryReference(string key, Entry entry)
    {
        if (!entry.ReleaseReference())
        {
            return;
        }

        bool removed = ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(
            new KeyValuePair<string, Entry>(key, entry));
        if (removed)
        {
            entry.Dispose();
        }
    }

    private sealed class Entry : IDisposable
    {
        // Zero is terminal: a new caller must wait for removal and acquire a fresh entry,
        // otherwise the last releaser could dispose a semaphore already reused by that caller.
        private int _references = 1;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddReference()
        {
            int references = Volatile.Read(ref _references);
            while (references != 0)
            {
                int observed = Interlocked.CompareExchange(
                    ref _references,
                    checked(references + 1),
                    references);
                if (observed == references)
                {
                    return true;
                }

                references = observed;
            }

            return false;
        }

        public bool ReleaseReference()
        {
            return Interlocked.Decrement(ref _references) == 0;
        }

        public void Dispose()
        {
            Semaphore.Dispose();
        }
    }

    private sealed class Lease(
        ProfileOperationGate owner,
        string key,
        Entry entry) : IDisposable
    {
        private ProfileOperationGate? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
        }
    }
}
