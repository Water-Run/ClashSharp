using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerShellViewModelTests
{
    [Theory]
    [InlineData(InstallerProductState.Available, "安装")]
    [InlineData(InstallerProductState.Installed, "修复")]
    public async Task OrdinaryProductStateSelectsItsFixedPrimaryAction(
        InstallerProductState productState,
        string actionText)
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => Task.FromResult(
                InstallerPresentationTestData.Readiness(productState)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.CanExecuteMutations);
        Assert.Equal(actionText, viewModel.PrimaryActionText);
        Assert.Equal("已验证", viewModel.StatusBadge);
        Assert.True(viewModel.PrimaryActionCommand.CanExecute(parameter: null));
        Assert.True(viewModel.IsPrimaryActionVisible);
        Assert.Equal(
            productState == InstallerProductState.Installed,
            viewModel.IsSecondaryActionVisible);
        Assert.False(viewModel.IsCancelActionVisible);
        Assert.Equal(
            productState == InstallerProductState.Installed,
            viewModel.HasSecondaryAction);
        Assert.Equal(
            productState == InstallerProductState.Installed,
            viewModel.SecondaryActionCommand.CanExecute(parameter: null));
        Assert.Single(viewModel.Capabilities);
    }

    [Fact]
    public async Task InstalledReadinessOffersRepairAndUninstallOnTheSameCard()
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness(
                InstallerProductState.Installed)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);

        await viewModel.InitializeAsync();

        Assert.Equal("修复", viewModel.PrimaryActionText);
        Assert.Equal("卸载", viewModel.SecondaryActionText);
        Assert.True(viewModel.HasSecondaryAction);
        Assert.True(viewModel.PrimaryActionCommand.CanExecute(parameter: null));
        Assert.True(viewModel.SecondaryActionCommand.CanExecute(parameter: null));
        Assert.True(viewModel.IsPrimaryActionVisible);
        Assert.True(viewModel.IsSecondaryActionVisible);
        Assert.False(viewModel.IsCancelActionVisible);

        await viewModel.SecondaryActionCommand.ExecuteAsync();

        Assert.Equal(new[] { InstallerOperation.Uninstall }, runtime.Operations);
        Assert.False(viewModel.HasSecondaryAction);
        Assert.False(viewModel.SecondaryActionCommand.CanExecute(parameter: null));
    }

    [Theory]
    [InlineData(InstallerOperation.Install, "继续安装")]
    [InlineData(InstallerOperation.Repair, "继续修复")]
    [InlineData(InstallerOperation.Uninstall, "继续卸载")]
    public async Task DurableRecoveryExposesOnlyItsExactPrimaryOperation(
        InstallerOperation operation,
        string expectedLabel)
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness(
                InstallerProductState.RecoveryRequired,
                recoveryOperation: operation)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);

        await viewModel.InitializeAsync();

        Assert.Equal(expectedLabel, viewModel.PrimaryActionText);
        Assert.True(viewModel.IsPrimaryActionVisible);
        Assert.False(viewModel.IsSecondaryActionVisible);
        Assert.False(viewModel.HasSecondaryAction);

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal([operation], runtime.Operations);
    }

    [Theory]
    [InlineData(InstallerProductState.Available, null)]
    [InlineData(InstallerProductState.Installed, null)]
    [InlineData(InstallerProductState.RecoveryRequired, InstallerOperation.Install)]
    [InlineData(InstallerProductState.RecoveryRequired, InstallerOperation.Repair)]
    [InlineData(InstallerProductState.RecoveryRequired, InstallerOperation.Uninstall)]
    public async Task BlockedReadinessKeepsEveryStateMutationCommandHidden(
        InstallerProductState productState,
        InstallerOperation? recoveryOperation)
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness(
                productState,
                canExecute: false,
                diagnosticCode: "installer.environment.windows_11_required",
                recoveryOperation: recoveryOperation)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.CanExecuteMutations);
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(parameter: null));
        Assert.False(viewModel.IsPrimaryActionVisible);
        Assert.False(viewModel.IsSecondaryActionVisible);
        Assert.False(viewModel.IsCancelActionVisible);
        Assert.False(viewModel.HasSecondaryAction);
        Assert.Equal("installer.environment.windows_11_required", viewModel.DiagnosticCode);
        Assert.Equal("审查中", viewModel.StatusBadge);
    }

    [Theory]
    [InlineData("unknown_product_state")]
    [InlineData("unready_capability")]
    [InlineData("bad_diagnostic")]
    [InlineData("missing_capabilities")]
    [InlineData("recovery_missing_operation")]
    [InlineData("recovery_unknown_operation")]
    [InlineData("ordinary_has_recovery_operation")]
    public async Task InvalidReadinessFailsClosed(string invalidCase)
    {
        InstallerRuntimeReadiness readiness = invalidCase switch
        {
            "unknown_product_state" => InstallerPresentationTestData.Readiness() with
            {
                ProductState = (InstallerProductState)999,
            },
            "unready_capability" => InstallerPresentationTestData.Readiness(
                capabilities: [new("发布清单", "尚未验证。", false)]),
            "bad_diagnostic" => InstallerPresentationTestData.Readiness(
                diagnosticCode: "raw exception text"),
            "missing_capabilities" => InstallerPresentationTestData.Readiness() with
            {
                Capabilities = null!,
            },
            "recovery_missing_operation" => InstallerPresentationTestData.Readiness(
                InstallerProductState.RecoveryRequired),
            "recovery_unknown_operation" => InstallerPresentationTestData.Readiness(
                InstallerProductState.RecoveryRequired,
                recoveryOperation: (InstallerOperation)999),
            "ordinary_has_recovery_operation" => InstallerPresentationTestData.Readiness() with
            {
                RecoveryOperation = InstallerOperation.Uninstall,
            },
            _ => throw new InvalidOperationException(),
        };
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => Task.FromResult(readiness),
        };
        using var viewModel = new InstallerShellViewModel(runtime);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.CanExecuteMutations);
        Assert.False(viewModel.HasSecondaryAction);
        Assert.False(viewModel.SecondaryActionCommand.CanExecute(parameter: null));
        Assert.Equal("installer.runtime.readiness_invalid", viewModel.DiagnosticCode);
        Assert.Equal("已阻止", viewModel.StatusBadge);
    }

    [Fact]
    public async Task ReadinessCapabilitiesAreSnapshotted()
    {
        var capabilities = new List<InstallerCapabilityStatus>
        {
            new("Windows 11+ x64", "已验证。", true),
        };
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness(
                capabilities: capabilities)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);

        await viewModel.InitializeAsync();
        capabilities.Clear();

        Assert.Single(viewModel.Capabilities);
    }

    [Fact]
    public async Task SuccessfulExecutionUsesFixedPrimaryOperationAndRequiresFreshReadiness()
    {
        var runtime = new ScriptedInstallerRuntime();
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal(new[] { InstallerOperation.Install }, runtime.Operations);
        Assert.Equal("已完成", viewModel.StatusBadge);
        Assert.Equal("安装状态已验证", viewModel.StatusTitle);
        Assert.Equal(100, viewModel.ProgressValue);
        Assert.False(viewModel.CanExecuteMutations);
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(parameter: null));
    }

    [Theory]
    [InlineData(InstallerExecutionOutcome.Blocked, false, "已阻止", "操作未开始。", "系统未被更改")]
    [InlineData(InstallerExecutionOutcome.Cancelled, true, "已取消", "操作已取消。", "同一安装器发布")]
    [InlineData(InstallerExecutionOutcome.Uncertain, true, "需要恢复", "必须重新检查并恢复。", "同一安装器发布")]
    [InlineData(InstallerExecutionOutcome.Failed, true, "失败", "需要恢复或诊断。", "同一安装器发布")]
    public async Task NonSuccessOutcomesRemainFailClosed(
        InstallerExecutionOutcome outcome,
        bool recoveryPending,
        string expectedBadge,
        string expectedProgress,
        string expectedDetail)
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Execute = (_, _, _) => Task.FromResult(InstallerPresentationTestData.Result(
                outcome,
                recoveryPending,
                InstallerTransactionPhase.Prepared)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal(expectedBadge, viewModel.StatusBadge);
        Assert.Equal(expectedProgress, viewModel.ProgressStatus);
        Assert.False(viewModel.CanExecuteMutations);
        Assert.Contains(
            expectedDetail,
            viewModel.StatusDetail,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, InstallerTransactionPhase.Verified)]
    [InlineData(false, InstallerTransactionPhase.Prepared)]
    public async Task InvalidSuccessResultIsRejected(
        bool recoveryPending,
        InstallerTransactionPhase phase)
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Execute = (_, _, _) => Task.FromResult(InstallerPresentationTestData.Result(
                recoveryPending: recoveryPending,
                phase: phase)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal("installer.runtime.result_invalid", viewModel.DiagnosticCode);
        Assert.Equal("失败", viewModel.StatusBadge);
        Assert.False(viewModel.CanExecuteMutations);
    }

    [Fact]
    public async Task RuntimeExceptionsAreSanitizedAtBothPresentationBoundaries()
    {
        var inspectFailure = new ScriptedInstallerRuntime
        {
            Inspect = _ => throw new IOException("secret path"),
        };
        using var inspectionViewModel = new InstallerShellViewModel(inspectFailure);
        await inspectionViewModel.InitializeAsync();
        Assert.Equal("installer.runtime.inspection_failed", inspectionViewModel.DiagnosticCode);
        Assert.DoesNotContain("secret", inspectionViewModel.StatusDetail, StringComparison.Ordinal);

        var executeFailure = new ScriptedInstallerRuntime
        {
            Execute = (_, _, _) => throw new IOException("secret path"),
        };
        using var executionViewModel = new InstallerShellViewModel(executeFailure);
        await executionViewModel.InitializeAsync();
        await executionViewModel.PrimaryActionCommand.ExecuteAsync();
        Assert.Equal("installer.runtime.execution_failed", executionViewModel.DiagnosticCode);
        Assert.DoesNotContain("secret", executionViewModel.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FatalInspectionFailurePropagatesAfterReleasingTheSingleFlightGate()
    {
        var cause = new FatalPresentationTestException("fatal test sentinel");
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => Task.FromException<InstallerRuntimeReadiness>(cause),
        };
        using var viewModel = new InstallerShellViewModel(runtime);

        FatalPresentationTestException exception =
            await Assert.ThrowsAsync<FatalPresentationTestException>(
                viewModel.InitializeAsync);

        Assert.Same(cause, exception);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.RefreshCommand.CanExecute(parameter: null));
    }

    [Fact]
    public async Task FatalExecutionFailurePropagatesAfterInvalidatingReadiness()
    {
        var cause = new FatalPresentationTestException("fatal test sentinel");
        var runtime = new ScriptedInstallerRuntime
        {
            Execute = (_, _, _) => Task.FromException<InstallerExecutionResult>(cause),
        };
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        FatalPresentationTestException exception =
            await Assert.ThrowsAsync<FatalPresentationTestException>(
                viewModel.PrimaryActionCommand.ExecuteAsync);

        Assert.Same(cause, exception);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.CanExecuteMutations);
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(parameter: null));
    }

    [Fact]
    public async Task CancelCommandCancelsTheOnlyActiveMutation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ScriptedInstallerRuntime
        {
            Execute = async (_, _, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            },
        };
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        Task execution = viewModel.PrimaryActionCommand.ExecuteAsync();
        await entered.Task;
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.CancelCommand.CanExecute(parameter: null));
        Assert.False(viewModel.IsPrimaryActionVisible);
        Assert.False(viewModel.IsSecondaryActionVisible);
        Assert.True(viewModel.IsCancelActionVisible);
        viewModel.CancelCommand.Execute(parameter: null);
        await execution;

        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsPrimaryActionVisible);
        Assert.False(viewModel.IsSecondaryActionVisible);
        Assert.False(viewModel.IsCancelActionVisible);
        Assert.Equal("已取消", viewModel.StatusBadge);
        Assert.Equal("installer.cancelled", viewModel.DiagnosticCode);
    }

    [Fact]
    public async Task ConcurrentRefreshIsIgnoredByTheSingleFlightGate()
    {
        var readiness = new TaskCompletionSource<InstallerRuntimeReadiness>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => readiness.Task,
        };
        using var viewModel = new InstallerShellViewModel(runtime);

        Task first = viewModel.InitializeAsync();
        Task second = viewModel.InitializeAsync();
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(1, runtime.InspectionCount);
        Assert.False(viewModel.IsPrimaryActionVisible);
        Assert.False(viewModel.IsSecondaryActionVisible);
        Assert.True(viewModel.IsCancelActionVisible);
        readiness.SetResult(InstallerPresentationTestData.Readiness());
        await first;

        Assert.True(viewModel.CanExecuteMutations);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsPrimaryActionVisible);
        Assert.False(viewModel.IsSecondaryActionVisible);
        Assert.False(viewModel.IsCancelActionVisible);
    }

    [Fact]
    public async Task QueuedProgressCannotOverwriteTheFinalOutcome()
    {
        var context = new QueuedSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var runtime = new ScriptedInstallerRuntime
            {
                Execute = (_, progress, _) =>
                {
                    progress.Report(InstallerProgress.Create(
                        InstallerTransactionPhase.PackageCommitted,
                        40,
                        "installer.progress.package"));
                    return Task.FromResult(InstallerPresentationTestData.Result());
                },
            };
            using var viewModel = new InstallerShellViewModel(runtime);
            await viewModel.InitializeAsync();

            await viewModel.PrimaryActionCommand.ExecuteAsync();
            Assert.Equal(100, viewModel.ProgressValue);
            Assert.Equal("操作完成。", viewModel.ProgressStatus);

            context.Drain();
            Assert.Equal(100, viewModel.ProgressValue);
            Assert.Equal("操作完成。", viewModel.ProgressStatus);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
