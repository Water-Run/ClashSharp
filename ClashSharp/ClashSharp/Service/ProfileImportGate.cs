using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Service;

/// <summary>Serializes complete import transactions for one normalized profile without blocking other profiles.</summary>
/// <remarks>
/// Invariants: An entry remains registered while a holder or waiter references it.
/// Thread safety: Safe for concurrent callers.
/// Side effects: Waits asynchronously and releases keyed semaphore entries after their last user exits.
/// </remarks>
internal sealed class ProfileImportGate
{
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);

    /// <summary>Asynchronously acquires the transaction lease for <paramref name="profileId"/>.</summary>
    public async ValueTask<IDisposable> EnterAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        Entry entry = AcquireEntryReference(profileId);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, profileId, entry);
        }
        catch
        {
            ReleaseEntryReference(profileId, entry);
            throw;
        }
    }

    private Entry AcquireEntryReference(string profileId)
    {
        while (true)
        {
            if (_entries.TryGetValue(profileId, out Entry? existing))
            {
                if (existing.TryAddReference())
                {
                    return existing;
                }

                continue;
            }

            Entry created = new();
            if (_entries.TryAdd(profileId, created))
            {
                return created;
            }

            created.Dispose();
        }
    }

    private void Release(string profileId, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseEntryReference(profileId, entry);
    }

    private void ReleaseEntryReference(string profileId, Entry entry)
    {
        if (!entry.ReleaseReference())
        {
            return;
        }

        bool removed = ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(
            new KeyValuePair<string, Entry>(profileId, entry));
        if (removed)
        {
            entry.Dispose();
        }
    }

    private sealed class Entry : IDisposable
    {
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
        ProfileImportGate owner,
        string profileId,
        Entry entry) : IDisposable
    {
        private ProfileImportGate? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(profileId, entry);
        }
    }
}
