using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerTerminalCancellationTests
{
    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task SuccessfulClearAcknowledgementFinishesObservationAfterUICancellation(InstallerOperation operation)
    {
        using var cancellation = new CancellationTokenSource();
        var scenario = new InstallerScenario
        {
            Environment = new(true, operation == InstallerOperation.Install ? null : InstallerTestData.Version, false, null),
            FinalClearResponseAction = _ => { cancellation.Cancel(); return Task.CompletedTask; },
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(InstallerTestData.Request(operation), null, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(InstallerTransactionPhase.Verified, result.LastDurablePhase);
        Assert.False(result.RecoveryPending);
        Assert.Null(scenario.Store.Current);
        Assert.Equal("journal.load", scenario.Events.Last(entry => entry.StartsWith("journal.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LateCancellationDoesNotHideAnUnverifiableClearObservation()
    {
        using var cancellation = new CancellationTokenSource();
        var scenario = new InstallerScenario();
        scenario.FinalClearResponseAction = _ =>
        {
            cancellation.Cancel();
            scenario.Store.LoadAction = _ => throw new IOException("post-clear observation unavailable");
            return Task.CompletedTask;
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(InstallerTestData.Request(), null, cancellation.Token);

        Assert.Equal(InstallerExecutionOutcome.Uncertain, result.Outcome);
        Assert.Equal("installer.transaction.clear_reload_failed", result.DiagnosticCode);
        Assert.True(result.RecoveryPending);
    }
}
