using ClashSharp.Presentation.Dialogs;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Verifies modal presentation admission and release behavior.</summary>
public sealed class DialogPresentationGateTests
{
    [Fact]
    public void NonmodalRefresh_DoesNotAcquireModalAdmission()
    {
        DialogPresentationGate gate = new();
        int callbacks = 0;
        Assert.True(gate.TryRefreshWindowPresentation(() => callbacks++));
        Assert.Equal(1, callbacks);
        Assert.True(gate.TryEnter(out IDisposable? lease));
        lease!.Dispose();
    }

    [Fact]
    public void NonmodalRefresh_DoesNotReleaseAnExistingModalLease()
    {
        DialogPresentationGate gate = new();
        Assert.True(gate.TryEnter(out IDisposable? lease));
        using IDisposable owned = lease!;
        Assert.True(gate.TryRefreshWindowPresentation(() => { }));
        Assert.False(gate.TryEnter(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedNonmodalRefresh_AfterWindowTeardown_DoesNotTouchNativeState(bool modalWasActive)
    {
        DialogPresentationGate gate = new();
        IDisposable? lease = null;
        if (modalWasActive) { Assert.True(gate.TryEnter(out lease)); }
        gate.MarkWindowClosing();
        Assert.False(gate.TryRefreshWindowPresentation(() => throw new InvalidOperationException("Native window is gone.")));
        lease?.Dispose();
        Assert.False(gate.TryRefreshWindowPresentation(() => throw new InvalidOperationException("Released lease cannot reopen the window.")));
        Assert.False(gate.TryEnter(out _));
    }

    [Fact]
    public void NonmodalRefreshFailure_DoesNotPoisonModalAdmission()
    {
        DialogPresentationGate gate = new();
        Assert.Throws<InvalidOperationException>(() => gate.TryRefreshWindowPresentation(() => throw new InvalidOperationException("Display update failed.")));
        Assert.True(gate.TryEnter(out IDisposable? lease));
        lease!.Dispose();
    }

    [Fact]
    public void PresentationRefresh_OnlyTouchesAnAdmittedLiveWindow()
    {
        DialogPresentationGate gate = new();
        int updates = 0;
        Assert.False(gate.TryRefreshPresentation(() => updates++));
        Assert.True(gate.TryEnter(out IDisposable? lease));
        using IDisposable owned = Assert.IsAssignableFrom<IDisposable>(lease);
        Assert.True(gate.TryRefreshPresentation(() => updates++));
        gate.MarkWindowClosing();
        Assert.False(gate.TryRefreshPresentation(() => throw new InvalidOperationException("Native window is gone.")));
        owned.Dispose();
        Assert.False(gate.TryRefreshPresentation(() => updates++));
        Assert.Equal(1, updates);
    }

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
