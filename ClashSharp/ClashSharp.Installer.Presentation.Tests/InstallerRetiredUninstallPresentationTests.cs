using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerRetiredUninstallPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitConsentPrecedesAnyBackendInvocationEvenWhenOrdinaryReadinessIsUnreadable(bool accepted)
    {
        var runtime = new Runtime();
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();
        Assert.False(viewModel.CanExecuteMutations);
        Assert.True(viewModel.IsRetiredUninstallActionVisible);
        TaskCompletionSource displayed = Observe(viewModel);
        Task execution = viewModel.RetiredUninstallCommand.ExecuteAsync();
        try
        {
            await displayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(viewModel.IsBusy);
            Assert.Equal(0, runtime.Calls);
            Assert.False(viewModel.IsCancelActionVisible);
            Assert.False(viewModel.RefreshCommand.CanExecute(null));
            Assert.False(viewModel.OwnerTransferCommand.CanExecute(null));
            Assert.False(viewModel.IsRetiredUninstallActionVisible);
            await viewModel.RetiredUninstallCommand.ExecuteAsync();
            Assert.Equal(0, runtime.Calls);
            (accepted ? viewModel.ConfirmRetiredUninstallCommand : viewModel.DeclineRetiredUninstallCommand).Execute(null);
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { viewModel.RequestCancellation(); await execution; }
        Assert.Equal(accepted ? 1 : 0, runtime.Calls);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsRetiredUninstallConfirmationVisible);
        Assert.False(viewModel.ConfirmRetiredUninstallCommand.CanExecute(null));
        Assert.Equal(accepted ? "已卸载此账户副本" : "操作已取消", viewModel.StatusTitle);
        Assert.Contains(accepted ? "共享服务保持原状" : "尚未开始", viewModel.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowCloseDuringConsentCancelsBeforeBackendAndDrainsTheGeneration()
    {
        var runtime = new Runtime();
        using var viewModel = new InstallerShellViewModel(runtime);
        TaskCompletionSource displayed = Observe(viewModel);
        Task execution = viewModel.RetiredUninstallCommand.ExecuteAsync();
        await displayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.RequestCancellation();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, runtime.Calls);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsRetiredUninstallConfirmationVisible);
        Assert.Equal("installer.cancelled", viewModel.DiagnosticCode);
    }

    [Fact]
    public async Task ConfirmedOperationRetainsBusyUntilCancelledBackendDrains()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime
        {
            Execute = async token =>
        {
            entered.TrySetResult();
            await drain.Task;
            token.ThrowIfCancellationRequested();
            return InstallerPresentationTestData.Result();
        }
        };
        using var viewModel = new InstallerShellViewModel(runtime);
        TaskCompletionSource displayed = Observe(viewModel);
        Task execution = viewModel.RetiredUninstallCommand.ExecuteAsync();
        try
        {
            await displayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.ConfirmRetiredUninstallCommand.Execute(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.RequestCancellation();
            Assert.True(viewModel.IsBusy);
            Assert.False(execution.IsCompleted);
        }
        finally { drain.TrySetResult(); await execution; }
        Assert.False(viewModel.IsBusy);
        Assert.Contains("卸载此账户副本", viewModel.StatusDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("installer.retired_uninstall.current_owner", "普通卸载")]
    [InlineData("installer.retired_uninstall.ordinary_state_pending", "使用账户")]
    [InlineData("installer.owner_transfer.pending", "接收服务的账户")]
    [InlineData("installer.retired_uninstall.candidate_mismatch", "同一个安装器")]
    public async Task DedicatedFailuresExplainTheCorrectRecoveryAction(string code, string detail)
    {
        var runtime = new Runtime { Execute = _ => throw new InstallerProtocolException(code) };
        using var viewModel = new InstallerShellViewModel(runtime);
        TaskCompletionSource displayed = Observe(viewModel);
        Task execution = viewModel.RetiredUninstallCommand.ExecuteAsync();
        await displayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ConfirmRetiredUninstallCommand.Execute(null);
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(code, viewModel.DiagnosticCode);
        Assert.Contains(detail, viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.True(viewModel.IsRetiredUninstallActionVisible);
    }

    [Fact]
    public void PreviewCompositionDoesNotExposeDedicatedRemoval()
    {
        using var viewModel = new InstallerShellViewModel(new ScriptedInstallerRuntime());
        Assert.False(viewModel.RetiredUninstallCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmRetiredUninstallCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DedicatedStartupCannotFallThroughToAnotherComposition(bool enabled)
    {
        var bootstrap = InstallerRetiredUninstallBootstrap.Create(new string('a', 64), 42);
        int calls = 0;
        int result = InstallerStartupRouter.Run(bootstrap.ToArguments(), _ => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), 2,
            runRetiredUninstall: enabled ? value => { Assert.Equal(bootstrap, value); calls++; return 17; }
        : null);
        Assert.Equal(enabled ? 17 : 2, result);
        Assert.Equal(enabled ? 1 : 0, calls);
    }

    [Theory]
    [InlineData("--RETIRED-UNINSTALL-HELPER")]
    [InlineData("--retired-uninstall-helper", "--verify-payload")]
    [InlineData("--owner-transfer-helper", "--retired-session")]
    [InlineData("--machine-helper", "--retired-uninstall-helper")]
    [InlineData("ui", "--retired-session")]
    public void AmbiguousReservedUninstallArgumentsNeverOpenUi(params string[] args) =>
        Assert.Equal(2, InstallerStartupRouter.Run(args, _ => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), 2));

    private static TaskCompletionSource Observe(InstallerShellViewModel viewModel)
    {
        var displayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.IsRetiredUninstallConfirmationVisible) && viewModel.IsRetiredUninstallConfirmationVisible)
            {
                displayed.TrySetResult();
            }
        };
        return displayed;
    }

    private sealed class Runtime : IInstallerRuntime, IInstallerRetiredUninstallRuntime
    {
        internal int Calls;
        internal Func<CancellationToken, Task<InstallerExecutionResult>>? Execute;
        public bool SupportsRetiredUninstall => true;
        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken) =>
            throw new InstallerProtocolException("installer.transaction.root_verification_failed");
        public Task<InstallerExecutionResult> ExecuteAsync(InstallerOperation operation, IProgress<InstallerProgress> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Account-copy removal must use its dedicated runtime.");
        public Task<InstallerExecutionResult> UninstallRetiredAccountAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Execute?.Invoke(cancellationToken) ?? Task.FromResult(InstallerPresentationTestData.Result());
        }
    }
}
