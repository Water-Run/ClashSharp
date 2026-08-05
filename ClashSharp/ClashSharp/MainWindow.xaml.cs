using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Startup;
using ClashSharp.Model;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Service;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TriggerEventKind = global::ClashSharp.Model.Triggers.TriggerEventKind;

namespace ClashSharp;

/// <summary>Primary application window containing the NavigationView shell and content frame.</summary>
/// <remarks>
/// Invariants: The window enforces a minimum size of 800×600 DIP when native window-procedure subclassing is available.
/// Thread safety: Must be instantiated and accessed from the UI thread only.
/// Side effects: Best-effort native setup is capability-gated and restored on close when installed.
/// </remarks>
public sealed partial class MainWindow : Window, IPrimaryWindowActivationTarget
{
    /// <summary>Command-line argument used by automated UI validation to skip startup modal dialogs.</summary>
    internal const string SkipStartupDialogsArgument = "--skip-startup-dialogs";

    /// <summary>Minimum window width in device-independent pixels.</summary>
    private const int MinWindowWidth = 800;

    /// <summary>Minimum window height in device-independent pixels.</summary>
    private const int MinWindowHeight = 600;

    /// <summary>Win32 index constant for replacing the window procedure pointer.</summary>
    private const int GwlpWndproc = -4;

    /// <summary>Win32 message identifier for querying minimum and maximum sizing information.</summary>
    private const uint WmGetminmaxinfo = 0x0024;

    /// <summary>Registered shell message broadcast after Explorer recreates the taskbar.</summary>
    private const string TaskbarCreatedMessageName = "TaskbarCreated";

    private readonly IApplicationLifetimeRequestSink _startupLifetimeRequests;

    private readonly MainWindowComposition _composition;

    /// <summary>Delegate instance preventing garbage collection of the custom window procedure.</summary>
    private WndProcDelegate? _wndProcDelegate;

    /// <summary>Native capabilities available to optional shell decoration and tray integration.</summary>
    private readonly StartupShellNativeCapabilityState _nativeCapabilities = new();

    /// <summary>Runtime-only shell dependencies created after startup completes.</summary>
    private MainWindowComposition.Runtime? _runtime;

    /// <summary>Coordinates tray commands without coupling behavior to WinUI callbacks.</summary>
    private TrayCommandService _trayCommandService = null!;

    private ITriggerRuntimeEventPublisher _triggerEvents = null!;

    private ApplicationActionService _applicationActions = null!;

    private ApplicationLifecycleService _applicationLifecycle = null!;

    private StartupConflictSnapshot _startupConflicts = null!;

    /// <summary>Current app window used for close interception.</summary>
    private AppWindow? _appWindow;

    /// <summary>Native system tray integration.</summary>
    private MainWindowComposition.ITray? _trayService;

    /// <summary>Registered identifier for Explorer notification-area recreation.</summary>
    private uint _taskbarCreatedMessage;

    /// <summary>True only while this window was deliberately hidden behind a confirmed tray icon.</summary>
    private bool _hiddenToTray;

    /// <summary>True while a window or tray exit request is owned by the App lifetime.</summary>
    private bool _exitRequested;

    /// <summary>Allows the outer lifetime owner to close the window after shutdown completes.</summary>
    private bool _applicationExitApproved;

    /// <summary>Prevents reentrant native closing events from opening duplicate confirmation dialogs.</summary>
    private bool _closePromptActive;

    /// <summary>Owns the once-only transition into post-startup dialogs and trigger publication.</summary>
    private readonly StartupFlowSchedulingGate _startupFlow = new();

    /// <summary>True after startup invariants have completed and runtime pages are safe to expose.</summary>
    private bool _runtimeReady;

    private readonly CancellationTokenSource _windowLifetime = new();
    private Task? _mihomoServiceStatusRefreshTask;

    /// <summary>Initializes the minimal visible startup shell without exposing runtime navigation.</summary>
    internal MainWindow(IApplicationLifetimeRequestSink startupLifetimeRequests)
        : this(startupLifetimeRequests, MainWindowComposition.Create())
    {
    }

    internal MainWindow(
        IApplicationLifetimeRequestSink startupLifetimeRequests,
        MainWindowComposition composition)
    {
        _startupLifetimeRequests = startupLifetimeRequests
            ?? throw new ArgumentNullException(nameof(startupLifetimeRequests));
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
        StartupShellSetupPolicy.TryRun(_composition.ApplyStartupAccentColor);
        InitializeComponent();
        Closed += OnWindowClosed;
        StartupShellSetupPolicy.TryRun(InitializeStartupStatus);
        InitializeWindowNativeCapabilities();
        _nativeCapabilities.TryRunWindowHandleFeature(InitializeTitleBar);
    }

    /// <summary>Supplies runtime dependencies and unlocks navigation after critical startup steps finish.</summary>
    internal void CompleteStartup(
        ITriggerRuntimeEventPublisher triggerEvents,
        ApplicationActionService applicationActions,
        ApplicationLifecycleService applicationLifecycle,
        TrayCommandService trayCommandService,
        StartupConflictSnapshot startupConflicts)
    {
        if (_runtimeReady)
        {
            throw new InvalidOperationException("The main window startup shell is already complete.");
        }

        _triggerEvents = triggerEvents ?? throw new ArgumentNullException(nameof(triggerEvents));
        _applicationActions = applicationActions ?? throw new ArgumentNullException(nameof(applicationActions));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _trayCommandService = trayCommandService ?? throw new ArgumentNullException(nameof(trayCommandService));
        _startupConflicts = startupConflicts ?? throw new ArgumentNullException(nameof(startupConflicts));
        _runtime = _composition.CreateRuntime();
        _runtime.ApplyTheme((FrameworkElement)Content);
        NavView.DataContext = _runtime.ViewModel;

        _runtimeReady = true;
        StartupOverlay.Visibility = Visibility.Collapsed;
        NavView.Visibility = Visibility.Visible;
        NavView.IsEnabled = true;
        NavView.SelectedItem = NavMasterControlItem;
        NavigateToTag("MasterControl");
        _nativeCapabilities.TryCreateWindowMessageFeature(
            CreateTrayService,
            out _trayService);
        if (_trayService is not null)
        {
            _mihomoServiceStatusRefreshTask =
                RefreshMihomoServiceStatusForTrayAsync(_windowLifetime.Token);
        }

        _ = _startupFlow.TrySchedule(
            _runtimeReady,
            () => DispatcherQueue.TryEnqueue(RunStartupFlowOnDispatcher));
    }

    /// <summary>Keeps the startup shell visible and presents a stable diagnostic after startup stops.</summary>
    internal void ShowStartupFailure(string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        _runtimeReady = false;
        NavView.IsEnabled = false;
        NavView.Visibility = Visibility.Collapsed;
        StartupOverlay.Visibility = Visibility.Visible;
        StartupProgressRing.IsActive = false;
        StartupProgressRing.Visibility = Visibility.Collapsed;
        bool installerTransactionBlocked =
            InstallerTransactionStartupGate.IsBlockingDiagnosticCode(diagnostic);
        StartupStatusText.Text = _composition.ResolveStartupText(
            installerTransactionBlocked
                ? "Startup.Shell.InstallerTransactionPending"
                : "Startup.Shell.Failed",
            installerTransactionBlocked
                ? "Clash# detected an unfinished Installer transaction. Close Clash#, rerun the same ClashSharp Installer, and choose Repair. Clash# will not start the proxy core, transparent proxy (TUN), or Mihomo service until Repair finishes."
                : "Clash# could not finish starting.");
        StartupDiagnosticText.Text = diagnostic;
        StartupDiagnosticText.Visibility = Visibility.Visible;
        PrimaryWindowActivation.BringToFront(this);
    }

    private void InitializeStartupStatus()
    {
        StartupStatusText.Text = _composition.ResolveStartupText(
            "Startup.Shell.Starting",
            "Clash# is starting…");
    }

    bool IPrimaryWindowActivationTarget.IsMinimized =>
        _appWindow?.Presenter is OverlappedPresenter
        {
            State: OverlappedPresenterState.Minimized,
        };

    void IPrimaryWindowActivationTarget.Show()
    {
        _hiddenToTray = false;
        _appWindow?.Show();
    }

    void IPrimaryWindowActivationTarget.Restore()
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
        }
    }

    void IPrimaryWindowActivationTarget.Activate()
    {
        Activate();
    }

    /// <summary>Configures the custom title bar with transparent caption buttons.</summary>
    private void InitializeTitleBar(nint windowHandle)
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindow.Title = "Clash#";
        _appWindow.Closing += OnAppWindowClosing;
        SetTitleBar(AppTitleBar);
    }

    /// <summary>Subclasses the native window procedure to enforce minimum window dimensions.</summary>
    private void InitializeWindowNativeCapabilities()
    {
        if (!_nativeCapabilities.TryAcquireWindowHandle(
            () => WinRT.Interop.WindowNative.GetWindowHandle(this)))
        {
            return;
        }

        _wndProcDelegate = new WndProcDelegate(WindowProc);
        nint newWindowProcedure = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        if (!_nativeCapabilities.TryInstallWindowMessageHook(
            handle => SetWindowLong(handle, GwlpWndproc, newWindowProcedure)))
        {
            _wndProcDelegate = null;
            return;
        }

        if (SystemTrayAvailabilityPolicy.TryRegisterRecoveryMessage(
            () => RegisterWindowMessage(TaskbarCreatedMessageName),
            out uint taskbarCreatedMessage))
        {
            _taskbarCreatedMessage = taskbarCreatedMessage;
        }
    }

    /// <summary>Creates the native system tray icon and menu.</summary>
    private MainWindowComposition.ITray CreateTrayService(nint windowHandle)
    {
        return Runtime.CreateTray(
            windowHandle,
            tag => DispatcherQueue.TryEnqueue(() => NavigateFromTray(tag)),
            () => DispatcherQueue.TryEnqueue(RequestSafeExitFromTray),
            mode => DispatcherQueue.TryEnqueue(() => ApplyModeFromTray(mode)),
            isEnabled => DispatcherQueue.TryEnqueue(() => SetTransparentProxyFromTray(isEnabled)));
    }

    /// <summary>Handles NavigationView selection changes and navigates the content frame to the corresponding page.</summary>
    /// <param name="sender">The <see cref="NavigationView"/> raising the event. Not null.</param>
    /// <param name="args">Event data containing the newly selected item. Not null.</param>
    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_runtimeReady)
        {
            return;
        }

        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        NavigateToTag(tag);
    }

    /// <summary>Toggles the navigation pane when the shell title bar pane button is requested.</summary>
    /// <param name="sender">Title bar that raised the request. Not null.</param>
    /// <param name="args">Event payload supplied by WinUI. Not null.</param>
    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        if (!_runtimeReady)
        {
            return;
        }

        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    /// <summary>Navigates the content frame to the page represented by <paramref name="tag"/>.</summary>
    /// <param name="tag">Navigation item tag. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is null.</exception>
    private void NavigateToTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        Type? pageType = _runtime?.ViewModel.ResolvePageType(tag);

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    /// <summary>Navigates from tray callbacks and brings the window forward.</summary>
    private void NavigateFromTray(string tag)
    {
        if (FindNavigationItemByTag(tag) is NavigationViewItem item)
        {
            NavView.SelectedItem = item;
        }

        NavigateToTag(tag);
        PrimaryWindowActivation.BringToFront(this);
    }

    private NavigationViewItem? FindNavigationItemByTag(string tag)
    {
        return tag switch
        {
            "MasterControl" => NavMasterControlItem,
            "ProxyNodes" => NavProxyNodesItem,
            "Profiles" => NavProfilesItem,
            "Links" => NavLinksItem,
            "Rules" => NavRulesItem,
            "Triggers" => NavTriggersItem,
            "Connections" => NavConnectionsItem,
            "Statistics" => NavStatisticsItem,
            "Logs" => null,
            "About" => NavAboutItem,
            "Settings" => NavSettingsItem,
            _ => null,
        };
    }

    /// <summary>Restores the original window procedure and releases native resources on window close.</summary>
    /// <param name="sender">The window being closed. Not null.</param>
    /// <param name="args">Window close event arguments. Not null.</param>
    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Task? statusRefreshTask = _mihomoServiceStatusRefreshTask;
        _mihomoServiceStatusRefreshTask = null;
        MainWindowComposition.ITray? trayService = _trayService;
        _trayService = null;
        _taskbarCreatedMessage = 0;
        _hiddenToTray = false;
        AppWindow? appWindow = _appWindow;
        _appWindow = null;
        MainWindowComposition.Runtime? runtime = _runtime;
        _runtime = null;

        try
        {
            try
            {
                _ = StartupShellSetupPolicy.TryRun(_windowLifetime.Cancel);
            }
            finally
            {
                try
                {
                    if (appWindow is not null)
                    {
                        _ = StartupShellSetupPolicy.TryRun(
                            () => appWindow.Closing -= OnAppWindowClosing);
                    }

                    if (trayService is not null)
                    {
                        _ = StartupShellSetupPolicy.TryRun(trayService.Dispose);
                    }

                    if (runtime is not null)
                    {
                        _ = StartupShellSetupPolicy.TryRun(runtime.Dispose);
                    }
                }
                finally
                {
                    bool windowMessageHookReleased =
                        _nativeCapabilities.TryReleaseWindowMessageHook(
                            (handle, previousWindowProcedure) =>
                                SetWindowLong(handle, GwlpWndproc, previousWindowProcedure) != 0);
                    if (windowMessageHookReleased)
                    {
                        _wndProcDelegate = null;
                    }
                    else if (_wndProcDelegate is not null)
                    {
                        NativeWindowProcedureRoot.Retain(_wndProcDelegate);
                    }
                }
            }

            if (statusRefreshTask is not null)
            {
                await statusRefreshTask;
            }
        }
        finally
        {
            _ = StartupShellSetupPolicy.TryRun(_windowLifetime.Dispose);
        }
    }

    private async Task RefreshMihomoServiceStatusForTrayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Runtime.RefreshMihomoStatusAsync(cancellationToken);
            RefreshTrayMenuPreservingReachability();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Window shutdown owns cancellation of this best-effort status refresh.
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            TryReportMihomoStatusRefreshFailure(exception);
        }
    }

    private static void TryReportMihomoStatusRefreshFailure(Exception exception)
    {
        try
        {
            Debug.WriteLine(
                $"ClashSharp tray status refresh failed ({exception.GetType().FullName}).");
        }
        catch (Exception diagnosticException) when (
            StartupCompletionFailurePolicy.IsRecoverable(diagnosticException))
        {
            // Best-effort diagnostics must not replace the status-refresh failure they describe.
        }
    }

    /// <summary>Runs post-startup work after runtime readiness and one dispatcher turn.</summary>
    private async void RunStartupFlowOnDispatcher()
    {
        if (!_runtimeReady || _windowLifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await RunStartupFlowAsync();
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            Debug.WriteLine(
                $"ClashSharp startup flow failed ({exception.GetType().FullName}).");
        }
    }

    /// <summary>Shows startup conflicts and applies the configured startup proxy behavior.</summary>
    private async Task RunStartupFlowAsync()
    {
        bool skipStartupDialogs = ShouldSkipStartupDialogs();

        _triggerEvents.Publish(new TriggerRuntimeEvent(TriggerEventKind.AppEntered));

        if (!skipStartupDialogs && _startupConflicts.Issues.Count > 0)
        {
            await ShowStartupConflictDialogAsync(_startupConflicts.Issues);
        }

        if (!skipStartupDialogs && Runtime.ShowStartupGuideOnStartup)
        {
            await ShowStartupPromptDialogAsync();
        }

    }

    private static bool ShouldSkipStartupDialogs()
    {
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (string.Equals(argument, SkipStartupDialogsArgument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Shows detected startup conflicts in a repairable dialog.</summary>
    private async Task ShowStartupConflictDialogAsync(IReadOnlyList<StartupConflictIssue> issues)
    {
        XamlRoot? xamlRoot = GetDialogXamlRoot();
        if (xamlRoot is null)
        {
            Runtime.LogStartupWarning("Startup.Log.ConflictDialogSkipped");
            return;
        }

        await StartupConflictDialogPresenter.ShowAsync(
            xamlRoot,
            issues,
            Runtime.GetString,
            Runtime.ErrorSink,
            _windowLifetime.Token);
    }

    /// <summary>Shows the startup health prompt when enabled by settings.</summary>
    private async Task ShowStartupPromptDialogAsync()
    {
        XamlRoot? xamlRoot = GetDialogXamlRoot();
        if (xamlRoot is null)
        {
            Runtime.LogStartupWarning("Startup.Log.PromptSkipped");
            return;
        }

        await Runtime.ShowStartupGuideAsync(xamlRoot, _windowLifetime.Token);
    }

    /// <summary>Returns the top-level XAML root so dialogs are centered in the whole app window.</summary>
    private XamlRoot? GetDialogXamlRoot()
    {
        return Content is FrameworkElement root && root.XamlRoot is not null
            ? root.XamlRoot
            : ContentFrame.XamlRoot;
    }

    /// <summary>Prompts when closing while proxy takeover is active.</summary>
    /// <param name="sender">App window. Not null.</param>
    /// <param name="args">Closing event arguments. Not null.</param>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_applicationExitApproved)
        {
            return;
        }

        if (!_runtimeReady)
        {
            args.Cancel = true;
            if (!_exitRequested)
            {
                _exitRequested = _startupLifetimeRequests.TryRequest(
                    ApplicationLifetimeRequest.Exit("startup-shell"));
            }

            return;
        }

        if (_exitRequested)
        {
            args.Cancel = true;
            return;
        }

        CloseBehaviorMode closeBehavior = Runtime.CloseBehavior;
        bool isTrayAvailable =
            closeBehavior is CloseBehaviorMode.MinimizeToTray && IsTrayAvailableForHide();
        WindowCloseDisposition closeDisposition =
            WindowCloseBehaviorPolicy.Resolve(closeBehavior, isTrayAvailable);
        if (closeDisposition is WindowCloseDisposition.HideToTray)
        {
            args.Cancel = true;
            sender.Hide();
            _hiddenToTray = true;
            return;
        }

        if (closeDisposition is WindowCloseDisposition.RequestSafeExit)
        {
            args.Cancel = true;
            RequestApplicationExit("main-window");
            return;
        }

        bool proxyTakeoverActive = Runtime.IsProxyTakeoverActive();
        args.Cancel = true;
        if (_closePromptActive)
        {
            return;
        }

        _closePromptActive = true;
        ThemedContentDialog dialog = new()
        {
            Title = Runtime.GetString(proxyTakeoverActive ? "Close.ProxyActive.Title" : "Close.Confirm.Title"),
            Content = Runtime.GetString(proxyTakeoverActive ? "Close.ProxyActive.Message" : "Close.Confirm.Message"),
            PrimaryButtonText = Runtime.GetString("Command.Close"),
            CloseButtonText = Runtime.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        try
        {
            if (await dialog.ShowManagedAsync() is ContentDialogResult.Primary)
            {
                RequestApplicationExit("main-window-confirmation");
            }
        }
        finally
        {
            _closePromptActive = false;
        }
    }

    private bool IsTrayAvailableForHide()
    {
        return SystemTrayAvailabilityPolicy.CanHideToTray(
            _taskbarCreatedMessage,
            () => _trayService?.TryEnsureAvailable() == true);
    }

    private void RecoverTrayAfterShellRestart()
    {
        _ = SystemTrayAvailabilityPolicy.TryRefreshAndPreserveReachability(
            () => _trayService?.TryEnsureAvailable() == true,
            _hiddenToTray,
            () => PrimaryWindowActivation.BringToFront(this));
    }

    private void RefreshTrayMenuPreservingReachability()
    {
        _ = SystemTrayAvailabilityPolicy.TryRefreshAndPreserveReachability(
            () => _trayService?.RefreshMenu() == true,
            _hiddenToTray,
            () => PrimaryWindowActivation.BringToFront(this));
    }

    /// <summary>Applies a mode requested from the tray menu.</summary>
    private async void ApplyModeFromTray(ClashSharpMode mode)
    {
        MainWindowComposition.Runtime? runtime = _runtime;
        if (!_runtimeReady
            || runtime is null
            || _windowLifetime.IsCancellationRequested)
        {
            return;
        }

        CancellationToken cancellationToken = _windowLifetime.Token;
        try
        {
            if (mode == runtime.CurrentMode)
            {
                return;
            }

            if (await _trayCommandService.ApplyModeAsync(mode, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await NotifyAndTriggerModeAppliedAsync(
                    runtime.CurrentMode,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RefreshTrayMenuPreservingReachability();
        }
        catch (Exception exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportShellOperationFailureAsync(
                runtime.ErrorSink,
                "tray-apply-mode",
                exception);
        }
    }

    /// <summary>Sets transparent proxy preference from the tray menu.</summary>
    private async void SetTransparentProxyFromTray(bool isEnabled)
    {
        MainWindowComposition.Runtime? runtime = _runtime;
        if (!_runtimeReady
            || runtime is null
            || _windowLifetime.IsCancellationRequested)
        {
            return;
        }

        CancellationToken cancellationToken = _windowLifetime.Token;
        try
        {
            if (isEnabled == runtime.TransparentProxyEnabled)
            {
                return;
            }

            if (await _trayCommandService.SetTransparentProxyEnabledAsync(
                isEnabled,
                cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await NotifyAndTriggerModeAppliedAsync(
                    runtime.CurrentMode,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RefreshTrayMenuPreservingReachability();
        }
        catch (Exception exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportShellOperationFailureAsync(
                runtime.ErrorSink,
                "tray-set-transparent-proxy",
                exception);
        }
    }

    private async Task NotifyAndTriggerModeAppliedAsync(
        ClashSharpMode mode,
        CancellationToken cancellationToken)
    {
        await _applicationActions.PublishProxyModeAppliedAsync(mode, cancellationToken);
    }

    private static async Task ReportShellOperationFailureAsync(
        IApplicationErrorSink errorSink,
        string operationName,
        Exception exception)
    {
        try
        {
            await errorSink.ReportAsync(
                new ApplicationError(operationName, exception),
                CancellationToken.None);
        }
        catch (Exception sinkException) when (
            !ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
            // This async-void shell boundary has no independent secondary diagnostic channel.
        }
    }

    /// <summary>Requests safe exit from the tray without showing the close confirmation prompt.</summary>
    private void RequestSafeExitFromTray()
    {
        RequestApplicationExit("system-tray");
    }

    private void RequestApplicationExit(string source)
    {
        _exitRequested = _applicationLifecycle.RequestExit(source);
    }

    /// <summary>Re-enables window and tray exit commands after outer shutdown could not prepare disposal.</summary>
    internal void NotifyExitRequestFailed()
    {
        _exitRequested = false;
        RefreshTrayMenuPreservingReachability();
    }

    /// <summary>Allows the App-owned lifetime to close this window after host shutdown succeeds.</summary>
    internal void ApproveApplicationExit()
    {
        _applicationExitApproved = true;
    }

    private MainWindowComposition.Runtime Runtime =>
        _runtime ?? throw new InvalidOperationException("The main window runtime is not ready.");

    /// <summary>Custom window procedure that enforces minimum window size by handling WM_GETMINMAXINFO.</summary>
    /// <param name="hWnd">Native window handle.</param>
    /// <param name="uMsg">Win32 message identifier.</param>
    /// <param name="wParam">Message-specific parameter.</param>
    /// <param name="lParam">Message-specific parameter; points to <see cref="MINMAXINFO"/> for WM_GETMINMAXINFO.</param>
    /// <returns>The result of message processing.</returns>
    private nint WindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam)
    {
        if (_taskbarCreatedMessage != 0 && uMsg == _taskbarCreatedMessage)
        {
            RecoverTrayAfterShellRestart();
        }

        if (_trayService?.TryHandleWindowMessage(uMsg, wParam, lParam) == true)
        {
            return 0;
        }

        if (uMsg == WmGetminmaxinfo)
        {
            uint dpi = GetDpiForWindow(hWnd);
            MINMAXINFO info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.x = (MinWindowWidth * (int)dpi + 48) / 96;
            info.ptMinTrackSize.y = (MinWindowHeight * (int)dpi + 48) / 96;
            Marshal.StructureToPtr(info, lParam, true);
        }

        nint previousWindowProcedure = _nativeCapabilities.PreviousWindowProcedure;
        return previousWindowProcedure != 0
            ? CallWindowProc(previousWindowProcedure, hWnd, uMsg, wParam, lParam)
            : DefWindowProc(hWnd, uMsg, wParam, lParam);
    }

    #region Win32 Interop Declarations

    /// <summary>Delegate matching the Win32 WNDPROC signature for window procedure subclassing.</summary>
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Represents a point on screen in pixel coordinates.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        /// <summary>Horizontal coordinate.</summary>
        public int x;

        /// <summary>Vertical coordinate.</summary>
        public int y;
    }

    /// <summary>Contains minimum/maximum sizing and position information for a window.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        /// <summary>Reserved; do not use.</summary>
        public POINT ptReserved;

        /// <summary>Maximum width and height of the window when maximized.</summary>
        public POINT ptMaxSize;

        /// <summary>Position of the top-left corner when maximized.</summary>
        public POINT ptMaxPosition;

        /// <summary>Minimum tracking width and height of the window.</summary>
        public POINT ptMinTrackSize;

        /// <summary>Maximum tracking width and height of the window.</summary>
        public POINT ptMaxTrackSize;
    }

    /// <summary>Sets a window attribute identified by <paramref name="nIndex"/>, dispatching to the correct 32/64-bit API.</summary>
    /// <param name="hWnd">Target window handle.</param>
    /// <param name="nIndex">Attribute index (e.g. GWLP_WNDPROC).</param>
    /// <param name="dwNewLong">New attribute value.</param>
    /// <returns>The previous attribute value.</returns>
    private static nint SetWindowLong(nint hWnd, int nIndex, nint dwNewLong)
    {
        if (nint.Size == 8)
        {
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        }

        return new nint(SetWindowLong32(hWnd, nIndex, (int)dwNewLong));
    }

    /// <summary>32-bit SetWindowLong entry point.</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    /// <summary>64-bit SetWindowLongPtr entry point.</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    /// <summary>Passes a message to the specified previous window procedure.</summary>
    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Provides safe default processing when a previous window procedure is unavailable.</summary>
    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Registers the Explorer taskbar-recreation message for this process.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    /// <summary>Retrieves the DPI for the specified window.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    #endregion
}
