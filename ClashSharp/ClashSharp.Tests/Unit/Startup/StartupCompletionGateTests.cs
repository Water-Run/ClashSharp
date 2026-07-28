using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Tests.Unit.Startup;

public sealed class StartupCompletionGateTests
{
    [Fact]
    public void AvailableStartupShell_CompletesAcceptedContextOnlyOnce()
    {
        StartupCompletionGate<object> gate = new();
        object accepted = new();

        Assert.True(gate.TryAccept(
            accepted,
            hasAcceptedLifetimeRequest: false,
            out object? completion));
        Assert.Same(accepted, completion);

        Assert.False(gate.TryAccept(
            new object(),
            hasAcceptedLifetimeRequest: false,
            out completion));
        Assert.Null(completion);
    }

    [Fact]
    public void ReleasedLifetimeRequest_ResumesLatestDeferredContextWhenHostRemainsAttached()
    {
        StartupCompletionGate<object> gate = new();
        object superseded = new();
        object latest = new();

        Assert.False(gate.TryAccept(superseded, hasAcceptedLifetimeRequest: true, out _));
        Assert.False(gate.TryAccept(latest, hasAcceptedLifetimeRequest: true, out _));

        Assert.True(gate.TryResume(
            hasAcceptedLifetimeRequest: false,
            isHostAttached: true,
            out object? completion));
        Assert.Same(latest, completion);
    }

    [Fact]
    public void RetainedLifetimeRequest_DoesNotResumeDeferredContext()
    {
        StartupCompletionGate<object> gate = new();
        object deferred = new();
        Assert.False(gate.TryAccept(deferred, hasAcceptedLifetimeRequest: true, out _));

        Assert.False(gate.TryResume(
            hasAcceptedLifetimeRequest: true,
            isHostAttached: true,
            out object? completion));
        Assert.Null(completion);

        Assert.True(gate.TryResume(
            hasAcceptedLifetimeRequest: false,
            isHostAttached: true,
            out completion));
        Assert.Same(deferred, completion);
    }

    [Fact]
    public void DetachedRuntimeHost_DoesNotConsumeDeferredContext()
    {
        StartupCompletionGate<object> gate = new();
        object deferred = new();
        Assert.False(gate.TryAccept(deferred, hasAcceptedLifetimeRequest: true, out _));

        Assert.False(gate.TryResume(
            hasAcceptedLifetimeRequest: false,
            isHostAttached: false,
            out object? completion));
        Assert.Null(completion);

        Assert.True(gate.TryResume(
            hasAcceptedLifetimeRequest: false,
            isHostAttached: true,
            out completion));
        Assert.Same(deferred, completion);
    }

    [Fact]
    public void TerminalOutcome_DoesNotResumeDeferredContext()
    {
        StartupCompletionGate<object> gate = new();
        Assert.False(gate.TryAccept(new object(), hasAcceptedLifetimeRequest: true, out _));

        gate.Abandon();

        Assert.False(gate.TryResume(
            hasAcceptedLifetimeRequest: false,
            isHostAttached: true,
            out object? completion));
        Assert.Null(completion);
    }
}
