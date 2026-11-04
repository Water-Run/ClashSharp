using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
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
        await VerifyDetailsLayoutAsync();
        await VerifyPreferenceRowsAsync();
    }

    private static async Task VerifyPreferenceRowsAsync()
    {
#pragma warning disable WPF0001 // Exercise the production Fluent themes on the owning STA.
        ThemeMode previous = Application.Current.ThemeMode;
        try
        {
            foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                Application.Current.ThemeMode = theme;
                foreach (Size size in new[] { new Size(760, 760), new Size(640, 420) })
                {
                    var runtime = new Runtime { Preferences = true };
                    using var model = new InstallerShellViewModel(runtime);
                    MainWindow window = new(model) { Width = size.Width, Height = size.Height, ShowInTaskbar = false };
                    try
                    {
                        window.Show();
                        await WaitUntilAsync(() => runtime.Inspections == 1 && !model.IsBusy);
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                        FrameworkElement panel = (FrameworkElement)window.FindName("InstallPreferencesPanel");
                        // Expander derives from HeaderedContentControl; the two preset details are counted separately.
                        HeaderedContentControl[] rows = Descendants(panel).OfType<HeaderedContentControl>()
                            .Where(static row => row is not Expander).ToArray();
                        Assert.Equal(8, rows.Length);
                        Expander[] details = Descendants(panel).OfType<Expander>().ToArray();
                        Assert.Equal(["SystemDefaultsDetailsExpander", "NotificationPresetDetailsExpander"], details.Select(static detail => detail.Name));
                        foreach (Expander detail in details)
                        {
                            detail.IsExpanded = true;
                        }
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                        Assert.All(details, static detail => Assert.True(detail.ActualHeight > 0));
                        foreach (HeaderedContentControl row in rows)
                        {
                            FrameworkElement text = (FrameworkElement)row.Template.FindName("OptionTextPanel", row);
                            FrameworkElement action = (FrameworkElement)row.Template.FindName("OptionAction", row);
                            Rect labelBounds = text.TransformToAncestor(row).TransformBounds(new Rect(text.RenderSize));
                            Rect actionBounds = action.TransformToAncestor(row).TransformBounds(new Rect(action.RenderSize));
                            Assert.True(labelBounds.Right <= actionBounds.Left - 12, "Setting text must not overlap its switch or selector.");
                        }
                        CheckBox desktop = (CheckBox)window.FindName("DesktopShortcutToggle");
                        var peer = new CheckBoxAutomationPeer(desktop);
                        Assert.Equal("添加桌面快捷方式", peer.GetName());
                        Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle)).Toggle();
                        Assert.False(model.CreateDesktopShortcut);
                        model.ResetInstallPreferencesCommand.Execute(null);
                        Assert.True(desktop.IsChecked);
                        Assert.False(model.HasCustomInstallPreferences);
                        ScrollViewer scroll = (ScrollViewer)window.FindName("MainContent");
                        scroll.ScrollToBottom();
                        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                        Button primary = Descendants((DependencyObject)window.Content).OfType<Button>()
                            .Single(button => ReferenceEquals(button.Command, model.PrimaryActionCommand));
                        Rect primaryBounds = primary.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(primary.RenderSize));
                        Assert.InRange(primaryBounds.Bottom, 0, ((FrameworkElement)window.Content).ActualHeight);
                        Assert.True(primary.IsEnabled);
                        Assert.Equal(0, runtime.Mutations);
                    }
                    finally { window.Close(); }
                }
            }
        }
        finally { Application.Current.ThemeMode = previous; }
#pragma warning restore WPF0001
    }

    private static async Task VerifyDetailsLayoutAsync()
    {
        foreach (Size size in new[] { new Size(760, 520), new Size(640, 420) })
        {
            var runtime = new Runtime
            {
                Capabilities =
                [
                    new("系统要求", "当前桌面环境和系统符合安装要求。", true),
                    new("安装器签名", "安装器签名和版本信息已经验证。", true),
                    new("安装状态", "当前账户尚未安装 ClashSharp。", true),
                    new("未完成的操作", "没有需要继续的安装或维护操作。", true),
                    new("离线载荷", new string('长', 120), false),
                ],
            };
            using var model = new InstallerShellViewModel(runtime);
            MainWindow window = new(model) { Width = size.Width, Height = size.Height, ShowInTaskbar = false };
            try
            {
                window.Show();
                await WaitUntilAsync(() => runtime.Inspections == 1 && !model.IsBusy);
                Assert.Equal(5, model.Capabilities.Count);
                ((Expander)window.FindName("DetailsExpander")).IsExpanded = true;
                ScrollViewer scroll = (ScrollViewer)window.FindName("MainContent");
                scroll.ScrollToBottom();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
                ScrollBar bar = Assert.Single(Descendants(scroll).OfType<ScrollBar>(),
                    item => item.Orientation == Orientation.Vertical && item.IsVisible);
                FrameworkElement body = (FrameworkElement)window.FindName("ContentBody");
                Rect contentBounds = body.TransformToAncestor(scroll).TransformBounds(new Rect(body.RenderSize));
                Rect barBounds = bar.TransformToAncestor(scroll).TransformBounds(new Rect(bar.RenderSize));
                Assert.True(contentBounds.Right <= barBounds.Left - 4, "The native scrollbar must have a separate gutter outside the content.");
                ItemsControl checks = (ItemsControl)window.FindName("CapabilityChecks");
                TextBlock[] states = Descendants(checks).OfType<TextBlock>()
                    .Where(text => text.DataContext is InstallerCapabilityStatus status && text.Text == status.StateText)
                    .ToArray();
                Assert.Equal(5, states.Length);
                Assert.All(states.Where(text => ((InstallerCapabilityStatus)text.DataContext).IsReady),
                    text => Assert.Equal(Visibility.Collapsed, text.Visibility));
                Assert.Equal(Visibility.Visible, Assert.Single(states,
                    text => !((InstallerCapabilityStatus)text.DataContext).IsReady).Visibility);
                Assert.Equal(0, runtime.Mutations);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) { yield return descendant; }
        }
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

    private sealed class Runtime : IInstallerRuntime, IInstallerOwnerTransferRuntime, IInstallerRetiredUninstallRuntime, IInstallerPreferencesRuntime, IDisposable
    {
        internal bool HoldInspection { get; init; }
        internal bool Preferences { get; init; }
        internal int Inspections { get; private set; }
        internal int Mutations { get; private set; }
        internal int Disposals { get; private set; }
        internal IReadOnlyList<InstallerCapabilityStatus> Capabilities { get; init; } = [new("系统要求", "检查已完成。", true)];
        internal TaskCompletionSource ReleaseInspection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsOwnerTransfer => true;
        public bool SupportsRetiredUninstall => true;
        public bool SupportsInstallPreferences => Preferences;
        public Task<InstallerExecutionResult> InstallWithPreferencesAsync(InstallerPreferences preferences, IProgress<InstallerProgress> progress, CancellationToken cancellationToken) =>
            ExecuteAsync(InstallerOperation.Install, progress, cancellationToken);
        public Task<InstallerExecutionResult> CompletePreferencesAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The presentation test must never acquire installation authority.");
        public Task<InstallerExecutionResult> TransferWithPreferencesAsync(InstallerPreferences preferences,
            Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress> progress, CancellationToken cancellationToken) =>
            TransferAndExecuteAsync(confirm, progress, cancellationToken);

        public async Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken)
        {
            Inspections++;
            if (HoldInspection) { await ReleaseInspection.Task; }
            bool ready = Capabilities.All(static capability => capability.IsReady);
            return new(ready, "installer.runtime.ready", "可以安装", "检查已完成。", "1.0.0",
                InstallerProductState.Available, null, ready ? [InstallerOperation.Install] : [], Capabilities);
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
