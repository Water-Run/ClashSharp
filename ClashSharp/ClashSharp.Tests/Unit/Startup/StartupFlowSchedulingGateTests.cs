extern alias ClashSharpUi;

using StartupFlowSchedulingGate =
    ClashSharpUi::ClashSharp.Hosting.Startup.StartupFlowSchedulingGate;

namespace ClashSharp.Tests.Unit.Startup;

/// <summary>Verifies startup UI work is scheduled exactly once and only after runtime readiness.</summary>
public sealed class StartupFlowSchedulingGateTests
{
    [Fact]
    public void TrySchedule_RuntimeNotReady_DoesNotSchedule()
    {
        StartupFlowSchedulingGate gate = new();
        int attempts = 0;

        bool scheduled = gate.TrySchedule(
            runtimeReady: false,
            () =>
            {
                attempts++;
                return true;
            });

        Assert.False(scheduled);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void TrySchedule_RuntimeReady_SchedulesOnlyOnce()
    {
        StartupFlowSchedulingGate gate = new();
        int attempts = 0;

        bool first = gate.TrySchedule(
            runtimeReady: true,
            () =>
            {
                attempts++;
                return true;
            });
        bool second = gate.TrySchedule(
            runtimeReady: true,
            () =>
            {
                attempts++;
                return true;
            });

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void TrySchedule_DispatcherRejects_AllowsLaterRetry()
    {
        StartupFlowSchedulingGate gate = new();
        int attempts = 0;

        bool rejected = gate.TrySchedule(
            runtimeReady: true,
            () =>
            {
                attempts++;
                return false;
            });
        bool retried = gate.TrySchedule(
            runtimeReady: true,
            () =>
            {
                attempts++;
                return true;
            });

        Assert.False(rejected);
        Assert.True(retried);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void TrySchedule_RecoverableDispatcherFailure_AllowsLaterRetry()
    {
        StartupFlowSchedulingGate gate = new();

        bool rejected = gate.TrySchedule(
            runtimeReady: true,
            static () => throw new ObjectDisposedException("dispatcher"));
        bool retried = gate.TrySchedule(
            runtimeReady: true,
            static () => true);

        Assert.False(rejected);
        Assert.True(retried);
    }

    [Fact]
    public void TrySchedule_WrappedProcessFatalFailure_Propagates()
    {
        StartupFlowSchedulingGate gate = new();
        InvalidOperationException expected = new(
            "dispatcher wrapper",
            Activator.CreateInstance<OutOfMemoryException>());

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => gate.TrySchedule(
                runtimeReady: true,
                () => throw expected));

        Assert.Same(expected, actual);
    }
}
