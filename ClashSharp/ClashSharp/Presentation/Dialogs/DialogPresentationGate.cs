using System;
using System.Threading;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Rejects overlapping modal presentation attempts for one visual root.</summary>
/// <remarks>
/// Invariants: At most one lease is active at a time and disposing a lease releases the gate exactly once.
/// Thread safety: Admission and teardown state are atomic; native dismissal remains UI-thread-owned.
/// Side effects: The supplied native dismissal runs only for an active lease on a live window.
/// </remarks>
internal sealed class DialogPresentationGate
{
    private const int Entered = 1;
    private const int Closing = 2;
    private int _state;

    /// <summary>Attempts to acquire the modal-presentation lease without waiting.</summary>
    /// <param name="lease">Owned lease when admitted; otherwise null.</param>
    /// <returns>True only for the admitted caller.</returns>
    public bool TryEnter(out IDisposable? lease)
    {
        if (Interlocked.CompareExchange(ref _state, Entered, 0) != 0)
        {
            lease = null;
            return false;
        }

        lease = new Lease(this);
        return true;
    }

    /// <summary>Permanently stops admission and native dismissal before the owning window is destroyed.</summary>
    public void MarkWindowClosing() => Interlocked.Or(ref _state, Closing);

    /// <summary>Dismisses an active modal surface only while its window is still usable.</summary>
    /// <param name="dismiss">Native dismissal operation, invoked only on the owning UI thread.</param>
    /// <returns>True when the native operation was invoked.</returns>
    /// <remarks>Dismissal and window teardown must run on the same UI thread; admission remains thread-safe.</remarks>
    public bool TryDismiss(Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(dismiss);
        if (Volatile.Read(ref _state) != Entered) { return false; }
        dismiss();
        return true;
    }

    private void Exit()
    {
        Interlocked.And(ref _state, ~Entered);
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
