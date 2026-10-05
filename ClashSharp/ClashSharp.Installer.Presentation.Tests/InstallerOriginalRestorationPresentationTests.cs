using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerOriginalRestorationPresentationTests
{
    [Fact]
    public async Task CompletedPreservationClearlyReportsThatTheOriginalVersionRemains()
    {
        var runtime = Runtime(InstallerOperation.Repair, Preserved());
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal("已保留原安装", viewModel.StatusTitle);
        Assert.Contains("继续使用原版本", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.Equal("原安装已恢复。", viewModel.ProgressStatus);
        Assert.Equal(100, viewModel.ProgressValue);
        Assert.False(viewModel.CanExecuteMutations);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task OtherOperationsCannotClaimOriginalRestoration(InstallerOperation operation)
    {
        using var viewModel = new InstallerShellViewModel(Runtime(operation, Preserved()));
        await viewModel.InitializeAsync();

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal("installer.runtime.result_invalid", viewModel.DiagnosticCode);
        Assert.Equal("操作未完成", viewModel.StatusTitle);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("candidate-terminal")]
    [InlineData("wrong-diagnostic")]
    public async Task InconsistentPreservationReceiptCannotShowCompletion(string scenario)
    {
        InstallerExecutionResult result = scenario switch
        {
            "pending" => Preserved() with { RecoveryPending = true },
            "candidate-terminal" => Preserved() with { LastDurablePhase = InstallerTransactionPhase.Verified },
            _ => Preserved() with { DiagnosticCode = "installer.completed" },
        };
        using var viewModel = new InstallerShellViewModel(Runtime(InstallerOperation.Repair, result));
        await viewModel.InitializeAsync();

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal("installer.runtime.result_invalid", viewModel.DiagnosticCode);
    }

    [Fact]
    public async Task CancellationAfterOriginalTerminalStillRequestsRecovery()
    {
        InstallerExecutionResult result = Preserved() with
        {
            Outcome = InstallerExecutionOutcome.Cancelled,
            DiagnosticCode = "installer.cancelled",
            RecoveryPending = true,
        };
        using var viewModel = new InstallerShellViewModel(Runtime(InstallerOperation.Repair, result));
        await viewModel.InitializeAsync();

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal("installer.cancelled", viewModel.DiagnosticCode);
        Assert.Equal("操作已取消", viewModel.StatusTitle);
        Assert.Contains("继续", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.False(viewModel.CanExecuteMutations);
    }

    private static InstallerExecutionResult Preserved() => new(InstallerExecutionOutcome.Succeeded,
        "installer.recovery.original_restored", InstallerTransactionPhase.OriginalRestored, false);
    private static ScriptedInstallerRuntime Runtime(InstallerOperation operation, InstallerExecutionResult result) => new()
    {
        Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness(InstallerProductState.RecoveryRequired, operation)),
        Execute = (_, _, _) => Task.FromResult(result),
    };
}
