using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerOwnerTransferPresentationTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ExplicitInlineDecisionDrivesTheDedicatedRuntimeWithoutAnOrdinaryOperation(bool accepted, bool recovery)
    {
        var runtime = new TransferRuntime { Recovery = recovery };
        using var viewModel = new InstallerShellViewModel(runtime);
        var displayed = ObserveConfirmation(viewModel);
        // A foreign-owner ordinary journal can be unreadable; that is not transfer consent.
        await viewModel.InitializeAsync();
        Assert.False(viewModel.CanExecuteMutations);
        Assert.True(viewModel.IsOwnerTransferActionVisible);

        Task execution = viewModel.OwnerTransferCommand.ExecuteAsync();
        try
        {
            await displayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(viewModel.IsBusy);
            Assert.False(execution.IsCompleted);
            Assert.False(viewModel.IsCancelActionVisible);
            Assert.False(viewModel.IsOwnerTransferActionVisible);
            Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
            Assert.False(viewModel.RefreshCommand.CanExecute(null));
            Assert.Equal(recovery ? "继续切换到当前账户？" : "切换到当前账户？", viewModel.OwnerTransferConfirmationTitle);
            Assert.Null(runtime.Decision);
            (accepted ? viewModel.ConfirmOwnerTransferCommand : viewModel.DeclineOwnerTransferCommand).Execute(null);
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            viewModel.RequestCancellation();
            await execution;
        }

        Assert.Equal(accepted, runtime.Decision);
        Assert.False(viewModel.IsOwnerTransferConfirmationVisible);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.CanExecuteMutations);
        Assert.Equal(accepted ? "已完成" : "已取消", viewModel.StatusBadge);
        Assert.Equal(0, runtime.OrdinaryCalls);
        Assert.Equal(1, runtime.TransferCalls);
    }

    [Fact]
    public async Task WindowCancellationDismissesConsentAndKeepsBusyUntilBackendDrains()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new TransferRuntime { BeforeExit = () => { draining.TrySetResult(); return drain.Task; } };
        using var viewModel = new InstallerShellViewModel(runtime);
        TaskCompletionSource displayed = ObserveConfirmation(viewModel);
        Task execution = viewModel.OwnerTransferCommand.ExecuteAsync();
        try
        {
            await displayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.RequestCancellation();
            await draining.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(viewModel.IsOwnerTransferConfirmationVisible);
            Assert.True(viewModel.IsBusy);
            Assert.False(execution.IsCompleted);
            Assert.Null(runtime.Decision);
            Assert.False(viewModel.ConfirmOwnerTransferCommand.CanExecute(null));
        }
        finally
        {
            viewModel.RequestCancellation();
            drain.TrySetResult();
            await execution;
        }
        Assert.False(viewModel.IsBusy);
        Assert.Equal("installer.cancelled", viewModel.DiagnosticCode);
    }

    [Fact]
    public async Task RepeatedTransferClicksCannotCreateAnotherOfferOrGeneration()
    {
        var runtime = new TransferRuntime();
        using var viewModel = new InstallerShellViewModel(runtime);
        TaskCompletionSource displayed = ObserveConfirmation(viewModel);
        Task execution = viewModel.OwnerTransferCommand.ExecuteAsync();
        try
        {
            await displayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await viewModel.OwnerTransferCommand.ExecuteAsync();
            Assert.Equal(1, runtime.TransferCalls);
            viewModel.DeclineOwnerTransferCommand.Execute(null);
            await execution;
        }
        finally
        {
            viewModel.RequestCancellation();
            await execution;
        }
    }

    [Fact]
    public void PreviewRuntimeCannotExposeOrAcceptTransfer()
    {
        using var viewModel = new InstallerShellViewModel(new ScriptedInstallerRuntime());
        Assert.False(viewModel.IsOwnerTransferActionVisible);
        Assert.False(viewModel.OwnerTransferCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmOwnerTransferCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DedicatedStartupModeNeverFallsThroughToOrdinaryHelperOrUi(bool enabled)
    {
        var bootstrap = InstallerOwnerTransferBootstrap.Create(new string('a', 64), 4242);
        int calls = 0;
        int result = InstallerStartupRouter.Run(bootstrap.ToArguments(),
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), 2,
            runOwnerTransfer: enabled ? value => { Assert.Equal(bootstrap, value); calls++; return 17; }
        : null);
        Assert.Equal(enabled ? 17 : 2, result);
        Assert.Equal(enabled ? 1 : 0, calls);
    }

    [Theory]
    [InlineData("--OWNER-TRANSFER-HELPER")]
    [InlineData("--owner-transfer-helper", "--verify-payload")]
    [InlineData("--machine-helper", "--owner-transfer-helper")]
    [InlineData("ui", "--transfer-session")]
    public void MixedTransferModesCreateNoComposition(params string[] arguments)
    {
        int result = InstallerStartupRouter.Run(arguments,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), 2,
            runOwnerTransfer: _ => throw new InvalidOperationException());
        Assert.Equal(2, result);
    }

    private static TaskCompletionSource ObserveConfirmation(InstallerShellViewModel viewModel)
    {
        var displayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InstallerShellViewModel.IsOwnerTransferConfirmationVisible)
                && viewModel.IsOwnerTransferConfirmationVisible)
            {
                displayed.TrySetResult();
            }
        };
        return displayed;
    }

    private sealed class TransferRuntime : IInstallerRuntime, IInstallerOwnerTransferRuntime
    {
        internal bool Recovery { get; init; }
        internal bool? Decision { get; private set; }
        internal int TransferCalls { get; private set; }
        internal int OrdinaryCalls { get; private set; }
        internal Func<Task>? BeforeExit { get; init; }
        public bool SupportsOwnerTransfer => true;
        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken) =>
            Task.FromException<InstallerRuntimeReadiness>(new InstallerProtocolException("installer.transaction.root_verification_failed"));
        public Task<InstallerExecutionResult> ExecuteAsync(InstallerOperation operation, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            OrdinaryCalls++;
            throw new InvalidOperationException("Transfer must use its dedicated runtime.");
        }
        public async Task<InstallerExecutionResult> TransferAndExecuteAsync(
            Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            TransferCalls++;
            try
            {
                // The real offer arrives from a pipe continuation off the UI thread.
                Decision = await Task.Run(() => confirm(new(Recovery), cancellationToken), cancellationToken);
                return Decision.Value ? InstallerPresentationTestData.Result()
                    : new(InstallerExecutionOutcome.Cancelled, "installer.owner_transfer.declined", null, false);
            }
            finally
            {
                if (BeforeExit is not null) { await BeforeExit(); }
            }
        }
    }
}
