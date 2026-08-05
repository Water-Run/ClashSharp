extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Diagnostics;
using InstallerTransactionStartupGate =
    ClashSharpUi::ClashSharp.Hosting.Startup.InstallerTransactionStartupGate;
using InstallerTransactionState =
    ClashSharpUi::ClashSharp.Service.InstallerTransactionState;

namespace ClashSharp.Tests.Unit.Startup;

/// <summary>Verifies the Installer marker gate stops all later mutation and shutdown paths.</summary>
public sealed class InstallerTransactionStartupGateTests
{
    /// <summary>A missing public marker leaves normal startup admission open.</summary>
    [Fact]
    public async Task ExecuteAsync_ClearState_AllowsStartup()
    {
        MutationAdmissionBarrier barrier = new();
        InstallerTransactionStartupGate gate = new(InstallerTransactionState.Clear, barrier);

        StartupStepResult result = await gate.ExecuteAsync(
            new AppLaunchRequest(string.Empty),
            CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        Assert.Null(result.DiagnosticCode);
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
        Assert.Equal("installer-transaction-gate", gate.Name);
        Assert.Equal(125, gate.Order);
    }

    /// <summary>A retained marker closes admission terminally and exposes one stable support code.</summary>
    [Fact]
    public async Task ExecuteAsync_PendingState_ClosesAdmissionAndReturnsFatal()
    {
        MutationAdmissionBarrier barrier = new();
        InstallerTransactionStartupGate gate = new(InstallerTransactionState.Pending, barrier);

        StartupStepResult result = await gate.ExecuteAsync(
            new AppLaunchRequest(string.Empty),
            CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        Assert.Equal(InstallerTransactionStartupGate.PendingDiagnosticCode, result.DiagnosticCode);
        Assert.True(RuntimeDiagnosticCode.IsStable(result.DiagnosticCode));
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
    }

    /// <summary>An ambiguous marker path is treated as a distinct fail-closed Installer state.</summary>
    [Fact]
    public async Task ExecuteAsync_InvalidState_ClosesAdmissionAndReturnsFatal()
    {
        MutationAdmissionBarrier barrier = new();
        InstallerTransactionStartupGate gate = new(InstallerTransactionState.Invalid, barrier);

        StartupStepResult result = await gate.ExecuteAsync(
            new AppLaunchRequest(string.Empty),
            CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        Assert.Equal(InstallerTransactionStartupGate.InvalidDiagnosticCode, result.DiagnosticCode);
        Assert.True(RuntimeDiagnosticCode.IsStable(result.DiagnosticCode));
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
    }

    /// <summary>Closing a fatal diagnostic shell after this gate cannot execute network shutdown.</summary>
    [Fact]
    public async Task ExecuteAsync_PendingThenHostShutdown_DoesNotInvokeNetworkCoordinator()
    {
        MutationAdmissionBarrier barrier = new();
        InstallerTransactionStartupGate gate = new(InstallerTransactionState.Pending, barrier);
        _ = await gate.ExecuteAsync(new AppLaunchRequest(string.Empty), CancellationToken.None);
        RecordingShutdownNetworkCoordinator network = new();
        RuntimeLifecycleCoordinator lifecycle = new(
            barrier,
            network,
            () => throw new InvalidOperationException("A terminal gate must not build a network intent."),
            []);

        RuntimeShutdownResult result = await lifecycle.ShutdownAsync(CancellationToken.None);

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, result.Outcome);
        Assert.Equal(0, network.CallCount);
    }

    private sealed class RecordingShutdownNetworkCoordinator : IRuntimeShutdownNetworkCoordinator
    {
        public int CallCount { get; private set; }

        public Task<MutationResult<NetworkTransitionResult>> ApplyShutdownAsync(
            NetworkIntent intent,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Network shutdown must remain unreachable.");
        }
    }
}
