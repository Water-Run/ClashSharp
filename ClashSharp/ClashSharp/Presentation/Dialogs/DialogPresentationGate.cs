using System;
using System.Threading;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Rejects overlapping modal presentation attempts for one visual root.</summary>
/// <remarks>
/// Invariants: At most one lease is active at a time and disposing a lease releases the gate exactly once.
/// Thread safety: Safe for concurrent callers.
/// Side effects: None beyond in-memory admission state.
/// </remarks>
internal sealed class DialogPresentationGate
{
    private int _isEntered;

    /// <summary>Attempts to acquire the modal-presentation lease without waiting.</summary>
    /// <param name="lease">Owned lease when admitted; otherwise null.</param>
    /// <returns>True only for the admitted caller.</returns>
    public bool TryEnter(out IDisposable? lease)
    {
        if (Interlocked.CompareExchange(ref _isEntered, 1, 0) != 0)
        {
            lease = null;
            return false;
        }

        lease = new Lease(this);
        return true;
    }

    private void Exit()
    {
        Volatile.Write(ref _isEntered, 0);
    }

    private sealed class Lease(DialogPresentationGate owner) : IDisposable
    {
        private DialogPresentationGate? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Exit();
        }
    }
}
