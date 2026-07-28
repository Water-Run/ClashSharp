using ClashSharp.Presentation.Dialogs;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Verifies modal presentation admission and release behavior.</summary>
public sealed class DialogPresentationGateTests
{
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
