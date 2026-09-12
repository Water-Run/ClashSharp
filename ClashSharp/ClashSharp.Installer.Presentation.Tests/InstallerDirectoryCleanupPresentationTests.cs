using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerDirectoryCleanupPresentationTests
{
    [Fact]
    public async Task SuccessfulUninstallExplainsRetainedDirectoriesWithoutClaimingFailureOrCompleteCleanup()
    {
        InstallerDirectoryCleanupReport report = CreateReport(role => role switch
        {
            InstallerDirectoryRole.ProgramFilesProduct => InstallerDirectoryCleanupDisposition.RetainedNonEmpty,
            InstallerDirectoryRole.ProgramDataProduct => InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership,
            _ => InstallerDirectoryCleanupDisposition.Deleted,
        });
        ScriptedInstallerRuntime runtime = CreateRuntime(InstallerOperation.Uninstall,
            InstallerPresentationTestData.Result() with { DirectoryCleanupReport = report });
        using var viewModel = new InstallerShellViewModel(runtime);
        await ExecuteAsync(viewModel, InstallerOperation.Uninstall);

        Assert.Equal("卸载已完成，部分目录已保留", viewModel.StatusTitle);
        Assert.Equal("已完成", viewModel.StatusBadge);
        Assert.Equal(100, viewModel.ProgressValue);
        Assert.Contains("程序目录（Program Files\\ClashSharp）：包含其他内容。", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("共享数据目录（ProgramData\\ClashSharp）：无法确认由安装器创建。", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("安装记录目录", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("自有空目录已清理", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("ACL", viewModel.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nonce", viewModel.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("账本", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.False(viewModel.CanExecuteMutations);
        Assert.False(viewModel.IsBusy);
        Assert.Equal([InstallerOperation.Uninstall], runtime.Operations);
    }

    [Fact]
    public async Task EveryRetainedRoleIsListedOnceWithItsFixedDirectoryLabel()
    {
        InstallerDirectoryCleanupReport report = CreateReport(_ => InstallerDirectoryCleanupDisposition.RetainedNonEmpty);
        using var viewModel = new InstallerShellViewModel(CreateRuntime(InstallerOperation.Uninstall,
            InstallerPresentationTestData.Result() with { DirectoryCleanupReport = report }));
        await ExecuteAsync(viewModel, InstallerOperation.Uninstall);

        string[] lines = viewModel.StatusDetail.Split(Environment.NewLine);
        string[] directories = lines.Where(line => line.EndsWith("：包含其他内容。", StringComparison.Ordinal)).ToArray();
        Assert.Equal(InstallerDirectoryCleanupReport.DirectoryCount, directories.Length);
        Assert.Equal(directories.Length, directories.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(directories, line => line.Contains("Installer\\v2）", StringComparison.Ordinal));
        Assert.Contains(directories, line => line.Contains("InstallerAuthority\\v1）", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(InstallerDirectoryCleanupDisposition.Deleted)]
    [InlineData(InstallerDirectoryCleanupDisposition.Missing)]
    public async Task FullyObservedCleanDirectoriesAreDistinguishedFromAnUnreportedUninstall(
        InstallerDirectoryCleanupDisposition disposition)
    {
        using var viewModel = new InstallerShellViewModel(CreateRuntime(InstallerOperation.Uninstall,
            InstallerPresentationTestData.Result() with { DirectoryCleanupReport = CreateReport(_ => disposition) }));
        await ExecuteAsync(viewModel, InstallerOperation.Uninstall);

        Assert.Equal("卸载已完成", viewModel.StatusTitle);
        Assert.Contains("自有空目录已清理或已不存在", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("已保留", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.Equal("已完成", viewModel.StatusBadge);
        Assert.Equal("卸载完成。", viewModel.ProgressStatus);
    }

    [Fact]
    public async Task UninstallWithoutAReportKeepsExistingTextAndDoesNotClaimDirectoryCleanup()
    {
        using var viewModel = new InstallerShellViewModel(CreateRuntime(InstallerOperation.Uninstall,
            InstallerPresentationTestData.Result()));
        await ExecuteAsync(viewModel, InstallerOperation.Uninstall);

        Assert.Equal("操作已完成", viewModel.StatusTitle);
        Assert.Equal("可以关闭安装器，或重新检查以管理此应用。", viewModel.StatusDetail);
        Assert.Equal("操作完成。", viewModel.ProgressStatus);
    }

    [Theory]
    [InlineData(InstallerExecutionOutcome.Blocked)]
    [InlineData(InstallerExecutionOutcome.Cancelled)]
    [InlineData(InstallerExecutionOutcome.Failed)]
    [InlineData(InstallerExecutionOutcome.Uncertain)]
    public async Task AReportCannotTurnANonSuccessResultIntoSuccessfulCleanup(InstallerExecutionOutcome outcome)
    {
        using var viewModel = new InstallerShellViewModel(CreateRuntime(InstallerOperation.Uninstall,
            InstallerPresentationTestData.Result(outcome) with
            {
                DirectoryCleanupReport = CreateReport(_ => InstallerDirectoryCleanupDisposition.Deleted),
            }));
        await ExecuteAsync(viewModel, InstallerOperation.Uninstall);

        AssertRejectedReport(viewModel);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public async Task ASuccessfulDifferentOperationCannotDisplayAnUninstallCleanupReport(InstallerOperation operation)
    {
        using var viewModel = new InstallerShellViewModel(CreateRuntime(operation,
            InstallerPresentationTestData.Result() with
            {
                DirectoryCleanupReport = CreateReport(_ => InstallerDirectoryCleanupDisposition.Deleted),
            }));
        await ExecuteAsync(viewModel, operation);

        AssertRejectedReport(viewModel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountSpecificActionsCannotClaimOrdinaryUninstallCleanup(bool retiredUninstall)
    {
        var runtime = new AccountActionRuntime();
        using var viewModel = new InstallerShellViewModel(runtime);
        var displayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, _) =>
        {
            if (retiredUninstall ? viewModel.IsRetiredUninstallConfirmationVisible : viewModel.IsOwnerTransferConfirmationVisible)
            {
                displayed.TrySetResult();
            }
        };
        Task execution = (retiredUninstall ? viewModel.RetiredUninstallCommand : viewModel.OwnerTransferCommand).ExecuteAsync();
        try
        {
            await displayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (retiredUninstall ? viewModel.ConfirmRetiredUninstallCommand : viewModel.ConfirmOwnerTransferCommand).Execute(null);
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            viewModel.RequestCancellation();
            await execution;
        }

        AssertRejectedReport(viewModel);
    }

    [Fact]
    public async Task ANewInspectionReplacesThePreviousRetainedDirectoryExplanation()
    {
        ScriptedInstallerRuntime runtime = CreateRuntime(InstallerOperation.Uninstall,
            InstallerPresentationTestData.Result() with
            {
                DirectoryCleanupReport = CreateReport(_ => InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership),
            });
        using var viewModel = new InstallerShellViewModel(runtime);
        await ExecuteAsync(viewModel, InstallerOperation.Uninstall);
        Assert.Contains("已保留", viewModel.StatusTitle, StringComparison.Ordinal);

        runtime.Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness());
        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal("可以安装", viewModel.StatusTitle);
        Assert.DoesNotContain("已保留", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.True(viewModel.CanExecuteMutations);
    }

    private static void AssertRejectedReport(InstallerShellViewModel viewModel)
    {
        Assert.Equal("installer.runtime.result_invalid", viewModel.DiagnosticCode);
        Assert.Equal("失败", viewModel.StatusBadge);
        Assert.Equal("操作未完成", viewModel.StatusTitle);
        Assert.DoesNotContain("自有空目录已清理", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.False(viewModel.CanExecuteMutations);
        Assert.False(viewModel.IsBusy);
    }

    private static InstallerDirectoryCleanupReport CreateReport(
        Func<InstallerDirectoryRole, InstallerDirectoryCleanupDisposition> disposition) =>
        new(Enum.GetValues<InstallerDirectoryRole>().Select(role => new InstallerDirectoryCleanupEntry(role, disposition(role))));

    private static ScriptedInstallerRuntime CreateRuntime(InstallerOperation operation, InstallerExecutionResult result) => new()
    {
        Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness(operation == InstallerOperation.Install
            ? InstallerProductState.Available : InstallerProductState.Installed)),
        Execute = (_, _, _) => Task.FromResult(result),
    };

    private static async Task ExecuteAsync(InstallerShellViewModel viewModel, InstallerOperation operation)
    {
        await viewModel.InitializeAsync();
        await (operation == InstallerOperation.Uninstall
            ? viewModel.SecondaryActionCommand : viewModel.PrimaryActionCommand).ExecuteAsync();
    }

    private sealed class AccountActionRuntime : IInstallerRuntime, IInstallerOwnerTransferRuntime, IInstallerRetiredUninstallRuntime
    {
        public bool SupportsOwnerTransfer => true;
        public bool SupportsRetiredUninstall => true;

        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken) =>
            Task.FromResult(InstallerPresentationTestData.Readiness());

        public Task<InstallerExecutionResult> ExecuteAsync(InstallerOperation operation,
            IProgress<InstallerProgress> progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<InstallerExecutionResult> TransferAndExecuteAsync(
            Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            bool accepted = await confirm(new InstallerOwnerTransferConfirmation(false), cancellationToken);
            Assert.True(accepted);
            return ResultWithReport();
        }

        public Task<InstallerExecutionResult> UninstallRetiredAccountAsync(
            IProgress<InstallerProgress> progress, CancellationToken cancellationToken) => Task.FromResult(ResultWithReport());

        private static InstallerExecutionResult ResultWithReport() => InstallerPresentationTestData.Result() with
        {
            DirectoryCleanupReport = CreateReport(_ => InstallerDirectoryCleanupDisposition.Deleted),
        };
    }
}
