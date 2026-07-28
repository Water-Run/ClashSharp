using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Service;
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

    /// <summary>Creates the startup-safe shell composition.</summary>
    public static MainWindowComposition Create()
    {
        return new MainWindowComposition(
            AppSettingsService.Instance,
            LocalizationService.Instance);
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

    /// <summary>Builds the runtime shell graph after application startup has completed.</summary>
    public Runtime CreateRuntime()
    {
        IApplicationErrorSink errorSink = ApplicationErrorSink.CreateDefault();
        return new Runtime(
            _settings,
            _localization,
            () => MihomoServiceManager.Instance,
            () => LogStorageService.Instance,
            () => TrayStatusService.Instance,
            RestartRequiredStateService.Instance,
            errorSink,
            StartupGuideComposition.Create(errorSink));
    }

    /// <summary>Runtime operations exposed to the WinUI shell after startup readiness.</summary>
    internal sealed class Runtime : IDisposable
    {
        private readonly AppSettingsService _settings;
        private readonly LocalizationService _localization;
        private readonly Func<MihomoServiceManager> _getMihomo;
        private readonly Func<LogStorageService> _getLogs;
        private readonly Func<TrayStatusService> _getTrayStatus;
        private readonly IStartupGuidePresenter _startupGuide;

        public Runtime(
            AppSettingsService settings,
            LocalizationService localization,
            Func<MihomoServiceManager> getMihomo,
            Func<LogStorageService> getLogs,
            Func<TrayStatusService> getTrayStatus,
            RestartRequiredStateService restartState,
            IApplicationErrorSink errorSink,
            IStartupGuidePresenter startupGuide)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _localization = localization ?? throw new ArgumentNullException(nameof(localization));
            _getMihomo = getMihomo ?? throw new ArgumentNullException(nameof(getMihomo));
            _getLogs = getLogs ?? throw new ArgumentNullException(nameof(getLogs));
            _getTrayStatus = getTrayStatus ?? throw new ArgumentNullException(nameof(getTrayStatus));
            ArgumentNullException.ThrowIfNull(restartState);
            ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
            _startupGuide = startupGuide ?? throw new ArgumentNullException(nameof(startupGuide));

            ViewModel = new MainWindowViewModel(
                new ShellLocalizationAdapter(_localization),
                CreatePageMap(),
                new ShellRestartStateAdapter(restartState));
        }

        /// <summary>Bindable shell state and navigation resolution.</summary>
        public MainWindowViewModel ViewModel { get; }

        /// <summary>Application diagnostic sink used by shell-owned asynchronous UI boundaries.</summary>
        public IApplicationErrorSink ErrorSink { get; }

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
            return _getMihomo().GetStatusAsync(cancellationToken);
        }

        /// <summary>Logs a localized warning emitted by startup-shell presentation.</summary>
        public void LogStartupWarning(string localizationKey)
        {
            _getLogs().AppendLog(
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
            MihomoServiceStatus serviceStatus = _getMihomo().GetLatestStatus();
            return TrayMenuStateBuilder.Build(
                CurrentMode,
                TransparentProxyEnabled,
                serviceStatus.IsInstalled,
                _getTrayStatus().GetLatestSnapshot(),
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
                    openPage,
                    safeExit,
                    applyMode,
                    setTransparentProxy));
        }

        /// <summary>Releases shell subscriptions owned by the view model.</summary>
        public void Dispose()
        {
            ViewModel.Dispose();
        }

        private static IReadOnlyDictionary<string, Type> CreatePageMap()
        {
            return new Dictionary<string, Type>
            {
                ["MasterControl"] = typeof(View.MasterControl),
                ["ProxyNodes"] = typeof(View.Proxies),
                ["Profiles"] = typeof(View.Profiles),
                ["Links"] = typeof(View.Links),
                ["Rules"] = typeof(View.Rules),
                ["Triggers"] = typeof(View.Triggers),
                ["Statistics"] = typeof(View.Statistics),
                ["Logs"] = typeof(View.Logs),
                ["About"] = typeof(View.About),
                ["Settings"] = typeof(View.Settings),
            };
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
