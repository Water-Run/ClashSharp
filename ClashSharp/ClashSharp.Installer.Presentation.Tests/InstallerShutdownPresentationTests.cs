using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerShutdownPresentationTests
{
    [Fact]
    public async Task ShutdownBeforeInitializationNeverInspectsOrMutatesTheMachine()
    {
        var runtime = new ScriptedInstallerRuntime();
        using var viewModel = new InstallerShellViewModel(runtime);

        viewModel.RequestShutdown();
        viewModel.RequestShutdown();
        await viewModel.InitializeAsync();
        await viewModel.RefreshCommand.ExecuteAsync();
        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal(0, runtime.InspectionCount);
        Assert.Empty(runtime.Operations);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.IsPrimaryActionVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownPreventsCommandsReenteringWhenTheActiveOperationFinishes(bool inspection)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ScriptedInstallerRuntime();
        int cancellations = 0;
        async Task FinishAcceptedOperationAsync(CancellationToken token)
        {
            using CancellationTokenRegistration registration = token.Register(() => cancellations++);
            entered.SetResult();
            // A native helper may finish successfully after cancellation was requested.
            await drain.Task;
        }
        if (inspection)
        {
            runtime.Inspect = async token =>
            {
                await FinishAcceptedOperationAsync(token);
                return InstallerPresentationTestData.Readiness();
            };
        }
        else
        {
            runtime.Execute = async (_, _, token) =>
            {
                await FinishAcceptedOperationAsync(token);
                return InstallerPresentationTestData.Result();
            };
        }
        using var viewModel = new InstallerShellViewModel(runtime);
        if (!inspection) { await viewModel.InitializeAsync(); }
        Task active = inspection ? viewModel.InitializeAsync() : viewModel.PrimaryActionCommand.ExecuteAsync();
        Task? reenteredRefresh = null;
        Task? reenteredMutation = null;
        bool completionObserved = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(viewModel.IsBusy) && !viewModel.IsBusy)
                {
                    completionObserved = true;
                    reenteredRefresh = viewModel.RefreshCommand.ExecuteAsync();
                    reenteredMutation = viewModel.PrimaryActionCommand.ExecuteAsync();
                }
            };

            viewModel.RequestShutdown();
            viewModel.RequestShutdown();
            Assert.True(viewModel.IsBusy);
            Assert.False(active.IsCompleted);
            Assert.Equal(1, cancellations);
            Assert.Equal(0, runtime.DisposeCount);
        }
        finally
        {
            drain.TrySetResult();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(completionObserved);
        Assert.NotNull(reenteredRefresh);
        Assert.NotNull(reenteredMutation);
        await Task.WhenAll(reenteredRefresh, reenteredMutation).WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.InitializeAsync();
        Assert.Equal(1, runtime.InspectionCount);
        Assert.Equal(inspection ? 0 : 1, runtime.Operations.Count);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
        Assert.False(viewModel.IsPrimaryActionVisible);

        viewModel.Dispose();
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task CancellingWithoutClosingStillAllowsAnExplicitRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable.");
            },
        };
        using var viewModel = new InstallerShellViewModel(runtime);
        Task active = viewModel.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.RequestCancellation();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness());

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal(2, runtime.InspectionCount);
        Assert.True(viewModel.PrimaryActionCommand.CanExecute(null));
        Assert.False(viewModel.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleShutdownOrDisposalRevokesPreviouslyReadyActions(bool dispose)
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => Task.FromResult(InstallerPresentationTestData.Readiness(
                productState: InstallerProductState.Installed)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.PrimaryActionCommand.CanExecute(null));
        Assert.True(viewModel.SecondaryActionCommand.CanExecute(null));

        if (dispose) { viewModel.Dispose(); }
        else { viewModel.RequestShutdown(); }
        await viewModel.PrimaryActionCommand.ExecuteAsync();
        await viewModel.SecondaryActionCommand.ExecuteAsync();
        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal(1, runtime.InspectionCount);
        Assert.Empty(runtime.Operations);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
        Assert.False(viewModel.SecondaryActionCommand.CanExecute(null));
        Assert.False(viewModel.IsPrimaryActionVisible);
        Assert.False(viewModel.IsSecondaryActionVisible);
    }
}
