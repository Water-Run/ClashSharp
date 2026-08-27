using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Navigation;
using ClashSharp.Service;
using ClashSharp.Settings;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;

namespace ClashSharp.Presentation.Composition;

/// <summary>Composition root for the main WinUI shell.</summary>
/// <remarks>
/// This type is the only main-window boundary allowed to adapt process-wide legacy services.
/// The window receives shell-oriented operations and does not locate or construct concrete services.
/// </remarks>
internal sealed class MainWindowComposition
{
    private readonly AppSettingsService _settings;
    private readonly LocalizationService _localization;

    private MainWindowComposition(
        AppSettingsService settings,
        LocalizationService localization)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <summary>Creates the startup-safe shell composition from explicit entry-point dependencies.</summary>
    public static MainWindowComposition CreateStartupShell(
        AppSettingsService settings,
        LocalizationService localization)
    {
        return new MainWindowComposition(settings, localization);
    }

    /// <summary>Applies the persisted accent configuration before the window becomes interactive.</summary>
    public void ApplyStartupAccentColor()
    {
        AppThemeService.ApplyAccentColor(
            _settings.AppAccentColorMode,
            _settings.AppAccentColorValue);
    }

    /// <summary>Resolves startup text without allowing unavailable resources to prevent shell creation.</summary>
    public string ResolveStartupText(string key, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(fallback);

        try
        {
            string value = _localization.GetString(key);
            return string.IsNullOrWhiteSpace(value) || StringComparer.Ordinal.Equals(value, key)
                ? fallback
                : value;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            return fallback;
        }
    }

    /// <summary>Runtime operations exposed to the WinUI shell after startup readiness.</summary>
    internal sealed class Runtime : IDisposable
    {
        private static readonly HashSet<string> TrayRelevantSettingKeys = new(StringComparer.Ordinal)
        {
            SettingsRegistry.Keys.CurrentMode.Value,
            SettingsRegistry.Keys.TransparentProxyEnabled.Value,
            SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon.Value,
            SettingsRegistry.Keys.TrayVisibleFeatureIds.Value,
        };

        private readonly AppSettingsService _settings;
        private readonly LocalizationService _localization;
        private readonly MihomoCoreService _mihomoCore;
        private readonly MihomoServiceManager _mihomoService;
        private readonly NetworkStateCoordinator _networkState;
        private readonly LogStorageService _logs;
        private readonly TrayStatusService _trayStatus;
        private readonly IStartupGuidePresenter _startupGuide;
        private readonly ShellNavigationService _navigation;
        private int _disposed;

        public Runtime(
            AppSettingsService settings,
            LocalizationService localization,
            MihomoCoreService mihomoCore,
            MihomoServiceManager mihomoService,
            NetworkStateCoordinator networkState,
            LogStorageService logs,
            TrayStatusService trayStatus,
            RestartRequiredStateService restartState,
            IApplicationErrorSink errorSink,
            StartupGuideComposition startupGuideComposition,
            IPageFactory pageFactory,
            ShellNavigationService navigation)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _localization = localization ?? throw new ArgumentNullException(nameof(localization));
            _mihomoCore = mihomoCore ?? throw new ArgumentNullException(nameof(mihomoCore));
            _mihomoService = mihomoService ?? throw new ArgumentNullException(nameof(mihomoService));
            _networkState = networkState ?? throw new ArgumentNullException(nameof(networkState));
            _logs = logs ?? throw new ArgumentNullException(nameof(logs));
            _trayStatus = trayStatus ?? throw new ArgumentNullException(nameof(trayStatus));
            ArgumentNullException.ThrowIfNull(restartState);
            ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
            ArgumentNullException.ThrowIfNull(startupGuideComposition);
            _startupGuide = startupGuideComposition.Create(errorSink);
            PageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
            _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

            ViewModel = new MainWindowViewModel(
                new ShellLocalizationAdapter(_localization),
                new ShellRestartStateAdapter(restartState));
            _settings.SettingChanged += Settings_SettingChanged;
            _networkState.VerifiedStateChanged += NetworkState_VerifiedStateChanged;
            _mihomoCore.UnexpectedExit += MihomoCore_UnexpectedExit;
        }

        /// <summary>Bindable shell state and navigation resolution.</summary>
        public MainWindowViewModel ViewModel { get; }

        /// <summary>Window-scoped page activator for typed routes.</summary>
        public IPageFactory PageFactory { get; }

        /// <summary>Window-scoped semantic navigation publisher.</summary>
        public IShellNavigationService Navigation => _navigation;

        /// <summary>Application diagnostic sink used by shell-owned asynchronous UI boundaries.</summary>
        public IApplicationErrorSink ErrorSink { get; }

        /// <summary>Raised after a relevant persisted setting or verified network state changes.</summary>
        public event EventHandler? TrayStateChanged;

        /// <summary>Whether the startup health guide is enabled.</summary>
        public bool ShowStartupGuideOnStartup => _settings.ShowStartupGuideOnStartup;

        /// <summary>Configured close behavior.</summary>
        public CloseBehaviorMode CloseBehavior => _settings.CloseBehaviorMode;

        /// <summary>Currently applied proxy mode.</summary>
        public ClashSharpMode CurrentMode => _settings.CurrentMode;

        /// <summary>Whether transparent proxy is currently enabled.</summary>
        public bool TransparentProxyEnabled => _settings.TransparentProxyEnabled;

        /// <summary>Applies the persisted theme to the supplied window root.</summary>
        public void ApplyTheme(FrameworkElement root)
        {
            ArgumentNullException.ThrowIfNull(root);
            AppThemeService.Apply(root, _settings.AppThemeMode);
        }

        /// <summary>Gets a localized string for shell-owned UI.</summary>
        public string GetString(string key)
        {
            return _localization.GetString(key);
        }

        /// <summary>Collects and presents one startup-health guide for the window lifetime.</summary>
        public Task ShowStartupGuideAsync(
            XamlRoot xamlRoot,
            CancellationToken cancellationToken)
        {
            return _startupGuide.ShowAsync(xamlRoot, cancellationToken);
        }

        /// <summary>Refreshes the cached Mihomo status used by the tray menu.</summary>
        public Task RefreshMihomoStatusAsync(CancellationToken cancellationToken)
        {
            return _mihomoService.GetStatusAsync(cancellationToken);
        }

        /// <summary>Logs a localized warning emitted by startup-shell presentation.</summary>
        public void LogStartupWarning(string localizationKey)
        {
            _logs.AppendLog(
                "Warning",
                "Startup",
                _localization.GetString(localizationKey),
                null);
        }

        /// <summary>Returns whether the current mode owns proxy routing.</summary>
        public bool IsProxyTakeoverActive()
        {
            return CurrentMode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
        }

        /// <summary>Builds a current tray-menu snapshot.</summary>
        public TrayMenuState BuildTrayMenuState()
        {
            MihomoServiceStatus serviceStatus = _mihomoService.GetLatestStatus();
            NetworkTransitionResult? networkState = _networkState.GetLatestVerifiedState();
            bool runtimeKnown = networkState is not null && networkState.Mode == CurrentMode;
            return TrayMenuStateBuilder.Build(
                CurrentMode,
                TransparentProxyEnabled,
                serviceStatus.IsInstalled,
                runtimeKnown,
                systemProxyEffective: runtimeKnown
                    && networkState!.SystemProxyEnabled
                    && _mihomoCore.IsRunning,
                tunEffective: runtimeKnown
                    && networkState!.TransparentProxyEnabled
                    && serviceStatus.IsReady,
                _trayStatus.GetLatestSnapshot(),
                _settings.TrayVisibleFeatureIds.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _localization.GetString);
        }

        /// <summary>Creates the native tray adapter for this window.</summary>
        public ITray CreateTray(
            nint windowHandle,
            Action<string> openPage,
            Action safeExit,
            Action<ClashSharpMode> applyMode,
            Action<bool> setTransparentProxy)
        {
            return new TrayAdapter(
                new SystemTrayService(
                    windowHandle,
                    BuildTrayMenuState,
                    () => _settings.TrayUseMonochromeInactiveIcon,
                    openPage,
                    safeExit,
                    applyMode,
                    setTransparentProxy));
        }

        /// <summary>Releases shell subscriptions owned by the view model.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _settings.SettingChanged -= Settings_SettingChanged;
            _networkState.VerifiedStateChanged -= NetworkState_VerifiedStateChanged;
            _mihomoCore.UnexpectedExit -= MihomoCore_UnexpectedExit;
            ViewModel.Dispose();
            _navigation.Dispose();
        }

        private void Settings_SettingChanged(object? sender, AppSettingChangedEventArgs e)
        {
            if (!TrayRelevantSettingKeys.Contains(e.Key))
            {
                return;
            }

            TrayStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void NetworkState_VerifiedStateChanged(object? sender, EventArgs e)
        {
            TrayStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void MihomoCore_UnexpectedExit(object? sender, MihomoCoreUnexpectedExitEventArgs e)
        {
            TrayStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Narrow native-tray contract required by the window message shell.</summary>
    internal interface ITray : IDisposable
    {
        bool TryEnsureAvailable();

        bool RefreshMenu();

        bool TryHandleWindowMessage(uint message, nint wParam, nint lParam);
    }

    private sealed class TrayAdapter : ITray
    {
        private readonly SystemTrayService _tray;

        public TrayAdapter(SystemTrayService tray)
        {
            _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        }

        public bool TryEnsureAvailable()
        {
            return _tray.TryEnsureAvailable();
        }

        public bool RefreshMenu()
        {
            return _tray.RefreshMenu();
        }

        public bool TryHandleWindowMessage(uint message, nint wParam, nint lParam)
        {
            return _tray.TryHandleWindowMessage(message, wParam, lParam);
        }

        public void Dispose()
        {
            _tray.Dispose();
        }
    }

    private sealed class ShellRestartStateAdapter : IShellRestartState
    {
        private readonly RestartRequiredStateService _state;

        public ShellRestartStateAdapter(RestartRequiredStateService state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public event EventHandler? RestartPendingChanged
        {
            add => _state.RestartPendingChanged += value;
            remove => _state.RestartPendingChanged -= value;
        }

        public bool IsRestartPending => _state.IsRestartPending;
    }
}
