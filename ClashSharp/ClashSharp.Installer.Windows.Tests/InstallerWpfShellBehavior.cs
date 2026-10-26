using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Windows.Tests;

// Compiles the production Window XAML and code-behind into the existing WPF test host.
// The runtime is synthetic: these interactions never acquire installation authority.
internal static class InstallerWpfShellBehavior
{
    internal static async Task VerifyAsync()
    {
        await VerifyConfirmationsAsync();
        await VerifyCloseAdmissionAsync();
    }

    private static async Task VerifyConfirmationsAsync()
    {
        var runtime = new Runtime();
        using var model = new InstallerShellViewModel(runtime);
        MainWindow window = new(model) { Width = 640, Height = 420, ShowInTaskbar = false };
        Task? active = null;
        try
        {
            window.Show();
            await WaitUntilAsync(() => runtime.Inspections == 1 && !model.IsBusy);
            foreach (bool transfer in new[] { true, false })
            {
                active = transfer ? model.OwnerTransferCommand.ExecuteAsync() : model.RetiredUninstallCommand.ExecuteAsync();
                string name = transfer ? "DeclineOwnerTransferButton" : "DeclineRetiredUninstallButton";
                Button decline = (Button)window.FindName(name);
                await WaitUntilAsync(() => decline.IsKeyboardFocused);
                Assert.False(model.IsOperationSummaryVisible);
                FrameworkElement root = (FrameworkElement)window.Content;
                Rect bounds = decline.TransformToAncestor(root).TransformBounds(new Rect(decline.RenderSize));
                Assert.InRange(bounds.Top, 0, root.ActualHeight);
                Assert.InRange(bounds.Bottom, 0, root.ActualHeight);
                Assert.NotNull(decline.Command);
                decline.Command.Execute(null);
                await active.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(model.IsOperationSummaryVisible);
                Assert.Equal(0, runtime.Mutations);
            }
        }
        finally
        {
            model.RequestShutdown();
            if (active is not null) { await active.WaitAsync(TimeSpan.FromSeconds(5)); }
            window.Close();
        }
        Assert.Equal(1, runtime.Disposals);
    }

    private static async Task VerifyCloseAdmissionAsync()
    {
        var runtime = new Runtime { HoldInspection = true };
        using var model = new InstallerShellViewModel(runtime);
        MainWindow window = new(model) { ShowInTaskbar = false };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        bool rejected = false;
        window.Show();
        try
        {
            await WaitUntilAsync(() => runtime.Inspections == 1 && model.IsBusy);
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(model.IsBusy) && !model.IsBusy)
                {
                    rejected = !model.RefreshCommand.CanExecute(null) && !model.PrimaryActionCommand.CanExecute(null);
                    _ = model.RefreshCommand.ExecuteAsync();
                    _ = model.PrimaryActionCommand.ExecuteAsync();
                }
            };
            window.Close();
            Assert.True(window.IsVisible);
            Assert.True(model.IsBusy);
            Assert.Equal(0, runtime.Disposals);
        }
        finally
        {
            runtime.ReleaseInspection.TrySetResult();
            model.RequestShutdown();
            await WaitUntilAsync(() => !model.IsBusy);
            if (window.IsVisible) { window.Close(); }
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(rejected);
        Assert.Equal(1, runtime.Inspections);
        Assert.Equal(0, runtime.Mutations);
        Assert.Equal(1, runtime.Disposals);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "The native shell did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    private sealed class Runtime : IInstallerRuntime, IInstallerOwnerTransferRuntime, IInstallerRetiredUninstallRuntime, IDisposable
    {
        internal bool HoldInspection { get; init; }
        internal int Inspections { get; private set; }
        internal int Mutations { get; private set; }
        internal int Disposals { get; private set; }
        internal TaskCompletionSource ReleaseInspection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsOwnerTransfer => true;
        public bool SupportsRetiredUninstall => true;

        public async Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken)
        {
            Inspections++;
            if (HoldInspection) { await ReleaseInspection.Task; }
            return new(true, "installer.runtime.ready", "可以安装", "检查已完成。", "1.0.0",
                InstallerProductState.Available, null, [InstallerOperation.Install], []);
        }

        public Task<InstallerExecutionResult> ExecuteAsync(InstallerOperation operation, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            Mutations++;
            throw new InvalidOperationException("The close and confirmation tests must not execute an installation.");
        }

        public async Task<InstallerExecutionResult> TransferAndExecuteAsync(Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            if (!await confirm(new(false), cancellationToken)) { throw new InstallerUserCancelledException("installer.test.declined"); }
            Mutations++;
            throw new InvalidOperationException("The confirmation test must decline account transfer.");
        }

        public Task<InstallerExecutionResult> UninstallRetiredAccountAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            Mutations++;
            throw new InvalidOperationException("The confirmation test must decline account removal.");
        }

        public void Dispose() => Disposals++;
    }
}
