using ClashSharp.Presentation.Dialogs;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Verifies modal presentation admission and release behavior.</summary>
public sealed class DialogPresentationGateTests
{
    [Fact]
    public void ClosingWithoutADialog_RejectsLaterPresentation()
    {
        DialogPresentationGate gate = new();
        gate.MarkWindowClosing();
        Assert.False(gate.TryEnter(out IDisposable? lease));
        Assert.Null(lease);
    }

    [Fact]
    public void ClosingWithAnActiveLease_ReleaseCannotReopenTheDestroyedWindow()
    {
        DialogPresentationGate gate = new();
        Assert.True(gate.TryEnter(out IDisposable? lease));
        gate.MarkWindowClosing();
        Assert.IsAssignableFrom<IDisposable>(lease).Dispose();
        Assert.False(gate.TryEnter(out _));
        Assert.False(gate.TryDismiss(() => throw new InvalidOperationException("Native window is gone.")));
    }

    [Fact]
    public void QueuedCancellation_AfterWindowTeardown_DoesNotTouchThePendingNativeDialog()
    {
        DialogPresentationGate gate = new();
        Assert.True(gate.TryEnter(out IDisposable? lease));
        using IDisposable owned = Assert.IsAssignableFrom<IDisposable>(lease);
        using CancellationTokenSource lifetime = new();
        Queue<Action> dispatcher = new();
        TaskCompletionSource showOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool nativeWindowDestroyed = false;
        int hideCalls = 0;
        using CancellationTokenRegistration registration = lifetime.Token.Register(() => dispatcher.Enqueue(() =>
        {
            if (!showOperation.Task.IsCompleted)
            {
                gate.TryDismiss(() =>
                {
                    hideCalls++;
                    Assert.False(nativeWindowDestroyed, "Hide would access a destroyed native window.");
                });
            }
        }));

        lifetime.Cancel();
        gate.MarkWindowClosing();
        nativeWindowDestroyed = true;
        Assert.False(showOperation.Task.IsCompleted);
        Assert.Single(dispatcher).Invoke();
        Assert.Equal(0, hideCalls);
        showOperation.SetResult();
    }

    [Fact]
    public void QueuedCancellation_WhileWindowIsAlive_DismissesAndKeepsAdmissionUntilCompletion()
    {
        DialogPresentationGate gate = new();
        Assert.True(gate.TryEnter(out IDisposable? lease));
        IDisposable owned = Assert.IsAssignableFrom<IDisposable>(lease);
        TaskCompletionSource showOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(gate.TryDismiss(() => showOperation.SetResult()));
        Assert.True(showOperation.Task.IsCompletedSuccessfully);
        Assert.False(gate.TryEnter(out _));
        owned.Dispose();
        Assert.False(gate.TryDismiss(() => throw new InvalidOperationException("No dialog remains.")));
        Assert.True(gate.TryEnter(out IDisposable? next));
        Assert.IsAssignableFrom<IDisposable>(next).Dispose();
    }

    /// <summary>Only one concurrent dialog can own a presentation root.</summary>
    [Fact]
    public void TryEnter_WhileLeaseIsActive_RejectsSecondCaller()
    {
        DialogPresentationGate gate = new();

        Assert.True(gate.TryEnter(out IDisposable? firstLease));
        Assert.False(gate.TryEnter(out IDisposable? rejectedLease));
        IDisposable admittedLease = Assert.IsAssignableFrom<IDisposable>(firstLease);
        Assert.Null(rejectedLease);

        admittedLease.Dispose();
    }

    /// <summary>Disposal, including repeated disposal, safely makes the gate reusable.</summary>
    [Fact]
    public void Lease_Dispose_ReleasesGateExactlyOnce()
    {
        DialogPresentationGate gate = new();
        Assert.True(gate.TryEnter(out IDisposable? firstLease));
        IDisposable admittedFirstLease = Assert.IsAssignableFrom<IDisposable>(firstLease);

        admittedFirstLease.Dispose();
        admittedFirstLease.Dispose();

        Assert.True(gate.TryEnter(out IDisposable? secondLease));
        Assert.IsAssignableFrom<IDisposable>(secondLease).Dispose();
    }
}
