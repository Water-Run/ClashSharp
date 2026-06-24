/*
 * Main Application Window
 * Hosts the native WinUI navigation shell, enforces minimum window dimensions, and coordinates localization updates
 *
 * @author: WaterRun
 * @file: MainWindow.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.View;
using ClashSharp.ViewModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp;

/// <summary>Primary application window containing the NavigationView shell and content frame.</summary>
/// <remarks>
/// Invariants: The window enforces a minimum size of 800×600 DIP via Win32 window-procedure subclassing.
/// Thread safety: Must be instantiated and accessed from the UI thread only.
/// Side effects: Subclasses the native window procedure on construction; restores the original procedure on close.
/// </remarks>
public sealed partial class MainWindow : Window
{
    /// <summary>Minimum window width in device-independent pixels.</summary>
    private const int MinWindowWidth = 800;

    /// <summary>Minimum window height in device-independent pixels.</summary>
    private const int MinWindowHeight = 600;

    /// <summary>Win32 index constant for replacing the window procedure pointer.</summary>
    private const int GwlpWndproc = -4;

    /// <summary>Win32 message identifier for querying minimum and maximum sizing information.</summary>
    private const uint WmGetminmaxinfo = 0x0024;

    /// <summary>Delegate instance preventing garbage collection of the custom window procedure.</summary>
    private WndProcDelegate? _wndProcDelegate;

    /// <summary>Previous window procedure pointer, restored during window cleanup.</summary>
    private nint _oldWndProc;

    /// <summary>Native window handle obtained during initialization.</summary>
    private nint _hWnd;

    /// <summary>Bindable shell view model used by navigation controls.</summary>
    private readonly MainWindowViewModel _viewModel;

    /// <summary>Current app window used for close interception.</summary>
    private AppWindow? _appWindow;

    /// <summary>Native system tray integration.</summary>
    private SystemTrayService? _trayService;

    /// <summary>True after the user confirms a proxy-active close prompt.</summary>
    private bool _isCloseConfirmed;

    /// <summary>True after startup conflict checks and startup behavior have been scheduled.</summary>
    private bool _startupFlowStarted;

    /// <summary>Initializes the main window, applies minimum size constraints, configures the title bar, and sets up navigation.</summary>
    public MainWindow()
    {
        _viewModel = new MainWindowViewModel(
            new ShellLocalizationAdapter(LocalizationService.Instance),
            CreatePageMap());
        InitializeComponent();
        AppThemeService.Apply((FrameworkElement)Content, AppSettingsService.Instance.AppThemeMode);
        AppThemeService.ApplyAccentColor(
            AppSettingsService.Instance.AppAccentColorMode,
            AppSettingsService.Instance.AppAccentColorValue);
        NavView.DataContext = _viewModel;
        InitializeWindowMinSize();
        InitializeTitleBar();

        NavView.SelectedItem = NavMasterControlItem;
        NavigateToTag("MasterControl");
        InitializeTray();

        Closed += OnWindowClosed;
        ContentFrame.Loaded += OnContentFrameLoaded;
    }

    /// <summary>Configures the custom title bar with transparent caption buttons.</summary>
    private void InitializeTitleBar()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindow.Title = "Clash#";
        _appWindow.Closing += OnAppWindowClosing;
        SetTitleBar(AppTitleBar);
    }

    /// <summary>Subclasses the native window procedure to enforce minimum window dimensions.</summary>
    private void InitializeWindowMinSize()
    {
        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _wndProcDelegate = new WndProcDelegate(WindowProc);
        _oldWndProc = SetWindowLong(_hWnd, GwlpWndproc,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    /// <summary>Initializes the native system tray icon and menu.</summary>
    private void InitializeTray()
    {
        _trayService = new SystemTrayService(
            _hWnd,
            BuildTrayMenuState,
            () => DispatcherQueue.TryEnqueue(() => NavigateFromTray("MasterControl", NavMasterControlItem)),
            () => DispatcherQueue.TryEnqueue(() => NavigateFromTray("Settings", NavSettingsItem)),
            () => DispatcherQueue.TryEnqueue(RequestSafeExitFromTray),
            mode => DispatcherQueue.TryEnqueue(() => ApplyModeFromTray(mode)),
            isEnabled => DispatcherQueue.TryEnqueue(() => SetTransparentProxyFromTray(isEnabled)));
    }

    /// <summary>Handles NavigationView selection changes and navigates the content frame to the corresponding page.</summary>
    /// <param name="sender">The <see cref="NavigationView"/> raising the event. Not null.</param>
    /// <param name="args">Event data containing the newly selected item. Not null.</param>
    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
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
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    /// <summary>Navigates the content frame to the page represented by <paramref name="tag"/>.</summary>
    /// <param name="tag">Navigation item tag. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is null.</exception>
    private void NavigateToTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        Type? pageType = _viewModel.ResolvePageType(tag);

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    /// <summary>Navigates from tray callbacks and brings the window forward.</summary>
    private void NavigateFromTray(string tag, NavigationViewItem item)
    {
        NavView.SelectedItem = item;
        NavigateToTag(tag);
        Activate();
    }

    /// <summary>Restores the original window procedure and releases native resources on window close.</summary>
    /// <param name="sender">The window being closed. Not null.</param>
    /// <param name="args">Window close event arguments. Not null.</param>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        ContentFrame.Loaded -= OnContentFrameLoaded;
        _trayService?.Dispose();
        _trayService = null;

        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow = null;
        }

        _viewModel.Dispose();
        RuntimeShutdownService.Shutdown();

        if (_hWnd != 0 && _oldWndProc != 0)
        {
            SetWindowLong(_hWnd, GwlpWndproc, _oldWndProc);
            _oldWndProc = 0;
        }

        _wndProcDelegate = null;
        _hWnd = 0;
    }

    /// <summary>Runs startup checks once after the content frame has entered the XAML tree.</summary>
    private async void OnContentFrameLoaded(object sender, RoutedEventArgs args)
    {
        if (_startupFlowStarted)
        {
            return;
        }

        _startupFlowStarted = true;
        ContentFrame.Loaded -= OnContentFrameLoaded;
        await RunStartupFlowAsync();
    }

    /// <summary>Shows startup conflicts and applies the configured startup proxy behavior.</summary>
    private async Task RunStartupFlowAsync()
    {
        AppSettingsService settings = AppSettingsService.Instance;
        if (settings.StartupConflictCheckEnabled)
        {
            IReadOnlyList<StartupConflictIssue> issues = StartupConflictDetectionService.Instance.CheckConflicts(settings.MixedPort);
            if (issues.Count > 0)
            {
                await ShowStartupConflictDialogAsync(issues);
            }
        }

        ClashSharpMode startupMode = StartupBehaviorService.ResolveStartupMode(settings.StartupBehaviorMode, settings.CurrentMode);
        try
        {
            NetworkTakeoverResult result = NetworkTakeoverService.Instance.ApplyMode(startupMode);
            settings.CurrentMode = result.Mode;
            LogStorageService.Instance.AppendLog("Info", "Startup", result.Message, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.FileNotFoundException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogStorageService.Instance.AppendLog("Warning", "Startup", "Startup proxy behavior failed.", exception.Message);
        }
    }

    /// <summary>Shows detected startup conflicts in a repairable dialog.</summary>
    private async Task ShowStartupConflictDialogAsync(IReadOnlyList<StartupConflictIssue> issues)
    {
        XamlRoot? xamlRoot = ContentFrame.XamlRoot;
        if (xamlRoot is null)
        {
            LogStorageService.Instance.AppendLog("Warning", "Startup", "Startup conflict dialog skipped because ContentFrame has no XamlRoot.", null);
            return;
        }

        await StartupConflictDialogPresenter.ShowAsync(xamlRoot, issues);
    }

    /// <summary>Prompts when closing while proxy takeover is active.</summary>
    /// <param name="sender">App window. Not null.</param>
    /// <param name="args">Closing event arguments. Not null.</param>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isCloseConfirmed || !IsProxyTakeoverActive())
        {
            return;
        }

        args.Cancel = true;
        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Close.ProxyActive.Title"),
            Content = LocalizationService.Instance.GetString("Close.ProxyActive.Message"),
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Close"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ContentFrame.XamlRoot,
        };

        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            _isCloseConfirmed = true;
            Close();
        }
    }

    /// <summary>Returns whether a proxy takeover mode is currently active.</summary>
    /// <returns>True when closing will stop active proxy routing.</returns>
    private static bool IsProxyTakeoverActive()
    {
        ClashSharpMode currentMode = AppSettingsService.Instance.CurrentMode;
        return currentMode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
    }

    /// <summary>Builds current tray menu state.</summary>
    private static TrayMenuState BuildTrayMenuState()
    {
        MihomoServiceStatus serviceStatus = MihomoServiceManager.Instance.GetStatus();
        return TrayMenuStateBuilder.Build(
            AppSettingsService.Instance.CurrentMode,
            AppSettingsService.Instance.TransparentProxyEnabled,
            serviceStatus.IsInstalled);
    }

    /// <summary>Applies a mode requested from the tray menu.</summary>
    private void ApplyModeFromTray(ClashSharpMode mode)
    {
        try
        {
            NetworkTakeoverResult result = NetworkTakeoverService.Instance.ApplyMode(mode);
            AppSettingsService.Instance.CurrentMode = result.Mode;
            LogStorageService.Instance.AppendLog("Info", "Tray", result.Message, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.FileNotFoundException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogStorageService.Instance.AppendLog("Error", "Tray", "Tray mode change failed.", exception.Message);
        }
        finally
        {
            _trayService?.RefreshMenu();
        }
    }

    /// <summary>Sets transparent proxy preference from the tray menu.</summary>
    private void SetTransparentProxyFromTray(bool isEnabled)
    {
        MihomoServiceStatus serviceStatus = MihomoServiceManager.Instance.GetStatus();
        if (isEnabled && !serviceStatus.IsInstalled)
        {
            AppSettingsService.Instance.TransparentProxyEnabled = false;
            _trayService?.RefreshMenu();
            return;
        }

        AppSettingsService.Instance.TransparentProxyEnabled = isEnabled;
        _trayService?.RefreshMenu();
    }

    /// <summary>Requests safe exit from the tray without showing the close confirmation prompt.</summary>
    private void RequestSafeExitFromTray()
    {
        _isCloseConfirmed = true;
        Close();
    }

    /// <summary>Creates the navigation tag to page-type mapping used by the shell view model.</summary>
    /// <returns>Immutable navigation page map keyed by NavigationView tag.</returns>
    private static IReadOnlyDictionary<string, Type> CreatePageMap()
    {
        return new Dictionary<string, Type>
        {
            ["MasterControl"] = typeof(View.MasterControl),
            ["ProxyNodes"] = typeof(View.Proxies),
            ["Profiles"] = typeof(View.Profiles),
            ["Links"] = typeof(View.Links),
            ["Rules"] = typeof(View.Rules),
            ["Statistics"] = typeof(View.Statistics),
            ["Logs"] = typeof(View.Logs),
            ["About"] = typeof(View.About),
            ["Settings"] = typeof(View.Settings),
        };
    }


    /// <summary>Custom window procedure that enforces minimum window size by handling WM_GETMINMAXINFO.</summary>
    /// <param name="hWnd">Native window handle.</param>
    /// <param name="uMsg">Win32 message identifier.</param>
    /// <param name="wParam">Message-specific parameter.</param>
    /// <param name="lParam">Message-specific parameter; points to <see cref="MINMAXINFO"/> for WM_GETMINMAXINFO.</param>
    /// <returns>The result of message processing.</returns>
    private nint WindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam)
    {
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

        return CallWindowProc(_oldWndProc, hWnd, uMsg, wParam, lParam);
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

    /// <summary>Retrieves the DPI for the specified window.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    #endregion
}
