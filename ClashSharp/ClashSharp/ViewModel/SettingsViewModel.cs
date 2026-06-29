/*
 * Settings ViewModel
 * Owns settings state transitions for the settings page without depending on WinUI controls
 *
 * @author: WaterRun
 * @file: ViewModel/SettingsViewModel.cs
 * @date: 2026-06-24
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Minimal storage contract required by <see cref="SettingsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations persist valid values immediately.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: Property setters may write to durable user settings.
/// </remarks>
internal interface ISettingsStore
{
    AppLanguage DisplayLanguage { get; set; }

    AppThemeMode AppThemeMode { get; set; }

    AppAccentColorMode AppAccentColorMode { get; set; }

    string AppAccentColorValue { get; set; }

    bool LaunchAtStartupEnabled { get; set; }

    bool TransparentProxyEnabled { get; set; }

    int MixedPort { get; set; }

    bool ConnectionSamplingEnabled { get; set; }

    int ConnectionSamplingIntervalSeconds { get; set; }

    bool StartupConflictCheckEnabled { get; set; }

    StartupBehaviorMode StartupBehaviorMode { get; set; }

    bool ShowStartupGuideOnStartup { get; set; }

    bool TriggersEnabled { get; set; }

    bool TriggerNotificationsEnabled { get; set; }

    CloseBehaviorMode CloseBehaviorMode { get; set; }

    bool TrayUseMonochromeInactiveIcon { get; set; }

    string TrayVisibleFeatureIds { get; set; }

    bool CheckStaleProxyOnStartup { get; set; }

    bool RestoreProxyOnExit { get; set; }

    MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; }

    bool MainlandChinaUrlBlockingEnabled { get; set; }

    bool NotificationEnabled { get; set; }

    NotificationLevel NotificationLevel { get; set; }

    string ConnectionTestUrl { get; set; }

    string ConnectionTestProxyUrl1 { get; set; }

    string ConnectionTestProxyUrl2 { get; set; }

    string ConnectionTestDirectUrl { get; set; }
}

/// <summary>Immutable proxy information snapshot used by <see cref="SettingsViewModel"/>.</summary>
/// <param name="ConfigPath">Managed core configuration path; never null.</param>
/// <param name="IsCoreBinaryAvailable">True when the bundled core binary exists.</param>
/// <param name="CoreBinaryPath">Expected core binary path; never null.</param>
/// <remarks>
/// Invariants: String values are never null.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
internal readonly record struct SettingsProxyInformation(
    string ConfigPath,
    bool IsCoreBinaryAvailable,
    string CoreBinaryPath);

/// <summary>One taskbar tray menu feature exposed in settings.</summary>
internal readonly record struct SettingsTrayFeatureDefinition(
    string Id,
    string TitleKey,
    string DescriptionKey,
    string Glyph);

/// <summary>One connection-test target result ready for dialog presentation.</summary>
internal sealed record ConnectionTestTargetResult(
    string Label,
    string Url,
    bool Succeeded,
    string StatusText,
    string LatencyText,
    int? LatencyMilliseconds);

/// <summary>Overall connection-test result used to style the dialog summary.</summary>
internal enum ConnectionTestSummaryState
{
    AllPassed,
    PartialFailed,
    AllFailed,
}

/// <summary>Connection-test report containing all target rows and a localized summary.</summary>
internal sealed record ConnectionTestReport(
    IReadOnlyList<ConnectionTestTargetResult> Results,
    string SummaryText,
    ConnectionTestSummaryState SummaryState);

/// <summary>Mihomo service control contract required by transparent proxy settings.</summary>
internal interface IMihomoServiceController
{
    /// <summary>Gets current service status.</summary>
    /// <returns>Current service status.</returns>
    MihomoServiceStatus GetStatus();

    /// <summary>Deploys the mihomo Windows service.</summary>
    /// <param name="cancellationToken">Cancels deployment wait when requested.</param>
    /// <returns>Updated service status.</returns>
    Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken);

    /// <summary>Uninstalls the mihomo Windows service.</summary>
    /// <param name="cancellationToken">Cancels uninstall wait when requested.</param>
    /// <returns>Updated service status.</returns>
    Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken);
}

/// <summary>Default test-friendly service controller used by legacy constructors.</summary>
internal sealed class AlwaysAvailableMihomoServiceController : IMihomoServiceController
{
    /// <summary>Shared controller instance.</summary>
    public static AlwaysAvailableMihomoServiceController Instance { get; } = new(key => key);

    private readonly Func<string, string> _getString;

    public AlwaysAvailableMihomoServiceController(Func<string, string> getString)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    public MihomoServiceStatus GetStatus()
    {
        return new MihomoServiceStatus(true, false, _getString("MihomoService.Status.Deployed"));
    }

    public Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(GetStatus());
    }

    public Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new MihomoServiceStatus(false, false, _getString("MihomoService.Status.NotDeployed")));
    }
}

/// <summary>Adapts <see cref="AppSettingsService"/> to the settings view model storage contract.</summary>
internal sealed class AppSettingsStore : ISettingsStore
{
    /// <summary>Underlying persistent settings service.</summary>
    private readonly AppSettingsService _settings;

    /// <summary>Initializes a new adapter over the provided settings service.</summary>
    /// <param name="settings">Persistent settings service. Must not be null.</param>
    public AppSettingsStore(AppSettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public AppLanguage DisplayLanguage
    {
        get => _settings.DisplayLanguage;
        set => _settings.DisplayLanguage = value;
    }

    public bool TransparentProxyEnabled
    {
        get => _settings.TransparentProxyEnabled;
        set => _settings.TransparentProxyEnabled = value;
    }

    public AppThemeMode AppThemeMode
    {
        get => _settings.AppThemeMode;
        set => _settings.AppThemeMode = value;
    }

    public AppAccentColorMode AppAccentColorMode
    {
        get => _settings.AppAccentColorMode;
        set => _settings.AppAccentColorMode = value;
    }

    public string AppAccentColorValue
    {
        get => _settings.AppAccentColorValue;
        set => _settings.AppAccentColorValue = value;
    }

    public bool LaunchAtStartupEnabled
    {
        get => _settings.LaunchAtStartupEnabled;
        set => _settings.LaunchAtStartupEnabled = value;
    }

    public int MixedPort
    {
        get => _settings.MixedPort;
        set => _settings.MixedPort = value;
    }

    public bool ConnectionSamplingEnabled
    {
        get => _settings.ConnectionSamplingEnabled;
        set => _settings.ConnectionSamplingEnabled = value;
    }

    public int ConnectionSamplingIntervalSeconds
    {
        get => _settings.ConnectionSamplingIntervalSeconds;
        set => _settings.ConnectionSamplingIntervalSeconds = value;
    }

    public bool StartupConflictCheckEnabled
    {
        get => _settings.StartupConflictCheckEnabled;
        set => _settings.StartupConflictCheckEnabled = value;
    }

    public StartupBehaviorMode StartupBehaviorMode
    {
        get => _settings.StartupBehaviorMode;
        set => _settings.StartupBehaviorMode = value;
    }

    public bool ShowStartupGuideOnStartup
    {
        get => _settings.ShowStartupGuideOnStartup;
        set => _settings.ShowStartupGuideOnStartup = value;
    }

    public bool TriggersEnabled
    {
        get => _settings.TriggersEnabled;
        set => _settings.TriggersEnabled = value;
    }

    public bool TriggerNotificationsEnabled
    {
        get => _settings.TriggerNotificationsEnabled;
        set => _settings.TriggerNotificationsEnabled = value;
    }

    public CloseBehaviorMode CloseBehaviorMode
    {
        get => _settings.CloseBehaviorMode;
        set => _settings.CloseBehaviorMode = value;
    }

    public bool TrayUseMonochromeInactiveIcon
    {
        get => _settings.TrayUseMonochromeInactiveIcon;
        set => _settings.TrayUseMonochromeInactiveIcon = value;
    }

    public string TrayVisibleFeatureIds
    {
        get => _settings.TrayVisibleFeatureIds;
        set => _settings.TrayVisibleFeatureIds = value;
    }

    public bool CheckStaleProxyOnStartup
    {
        get => _settings.CheckStaleProxyOnStartup;
        set => _settings.CheckStaleProxyOnStartup = value;
    }

    public bool RestoreProxyOnExit
    {
        get => _settings.RestoreProxyOnExit;
        set => _settings.RestoreProxyOnExit = value;
    }

    public MainlandChinaFeatureMode MainlandChinaFeatureMode
    {
        get => _settings.MainlandChinaFeatureMode;
        set => _settings.MainlandChinaFeatureMode = value;
    }

    public bool MainlandChinaUrlBlockingEnabled
    {
        get => _settings.MainlandChinaUrlBlockingEnabled;
        set => _settings.MainlandChinaUrlBlockingEnabled = value;
    }

    public bool NotificationEnabled
    {
        get => _settings.NotificationEnabled;
        set => _settings.NotificationEnabled = value;
    }

    public NotificationLevel NotificationLevel
    {
        get => _settings.NotificationLevel;
        set => _settings.NotificationLevel = value;
    }

    public string ConnectionTestUrl
    {
        get => _settings.ConnectionTestUrl;
        set => _settings.ConnectionTestUrl = value;
    }

    public string ConnectionTestProxyUrl1
    {
        get => _settings.ConnectionTestProxyUrl1;
        set => _settings.ConnectionTestProxyUrl1 = value;
    }

    public string ConnectionTestProxyUrl2
    {
        get => _settings.ConnectionTestProxyUrl2;
        set => _settings.ConnectionTestProxyUrl2 = value;
    }

    public string ConnectionTestDirectUrl
    {
        get => _settings.ConnectionTestDirectUrl;
        set => _settings.ConnectionTestDirectUrl = value;
    }
}

/// <summary>Owns user-editable settings state and persistence for the settings page.</summary>
/// <remarks>
/// Invariants: Numeric values exposed by properties are always within the same valid range enforced by <see cref="AppSettingsService"/>.
/// Thread safety: Not thread-safe; intended for UI-thread use.
/// Side effects: Set methods persist values and may trigger injected application callbacks.
/// </remarks>
internal sealed class SettingsViewModel : ObservableObject
{
    private const string DefaultAppAccentColorValue = "#FF0078D4";
    private const int DefaultMixedPort = 10000;
    private const int DefaultConnectionSamplingIntervalSeconds = 30;
    private const int MinConnectionSamplingIntervalSeconds = 3;
    private const int MaxConnectionSamplingIntervalSeconds = 300;
    private const int ConnectionTestTimeoutSeconds = 4;
    private const string DefaultConnectionTestUrl = "https://www.google.com/generate_204";
    private const string DefaultConnectionTestProxyUrl1 = "https://www.google.com";
    private const string DefaultConnectionTestProxyUrl2 = "https://github.com";
    private const string DefaultConnectionTestDirectUrl = "https://www.baidu.com";
    private const string DefaultTrayVisibleFeatureIds = "status,mode,pages,transparent-proxy,settings,safe-exit";

    public static IReadOnlyList<SettingsTrayFeatureDefinition> TrayFeatureDefinitions { get; } =
    [
        new("status", "Settings.Tray.Feature.Status", "Settings.Tray.Feature.Status.Description", "\uE946"),
        new("mode", "Settings.Tray.Feature.Mode", "Settings.Tray.Feature.Mode.Description", "\uE8AB"),
        new("pages", "Settings.Tray.Feature.Pages", "Settings.Tray.Feature.Pages.Description", "\uE8A7"),
        new("transparent-proxy", "Settings.Tray.Feature.TransparentProxy", "Settings.Tray.Feature.TransparentProxy.Description", "\uE968"),
        new("settings", "Settings.Tray.Feature.Settings", "Settings.Tray.Feature.Settings.Description", "\uE713"),
        new("safe-exit", "Settings.Tray.Feature.SafeExit", "Settings.Tray.Feature.SafeExit.Description", "\uE8BB"),
    ];

    private static readonly (string ResourceKey, string[] Hosts)[] KnownConnectionTestUrlHosts =
    [
        ("Settings.ConnectionTestUrl.Provider.Google", ["google.com"]),
        ("Settings.ConnectionTestUrl.Provider.GitHub", ["github.com"]),
        ("Settings.ConnectionTestUrl.Provider.Baidu", ["baidu.com"]),
        ("Settings.ConnectionTestUrl.Provider.Bilibili", ["bilibili.com", "b23.tv"]),
        ("Settings.ConnectionTestUrl.Provider.Zhihu", ["zhihu.com"]),
        ("Settings.ConnectionTestUrl.Provider.YouTube", ["youtube.com", "youtu.be"]),
        ("Settings.ConnectionTestUrl.Provider.ChatGPT", ["chatgpt.com", "chat.openai.com"]),
        ("Settings.ConnectionTestUrl.Provider.OpenAI", ["openai.com", "platform.openai.com", "api.openai.com"]),
    ];

    /// <summary>Persistent settings store used by this view model.</summary>
    private readonly ISettingsStore _settings;

    /// <summary>Callback invoked when the display language changes.</summary>
    private readonly Action<AppLanguage> _applyLanguage;

    /// <summary>Callback invoked when the display style changes.</summary>
    private readonly Action<AppThemeMode> _applyTheme;

    /// <summary>Callback invoked when the application accent color changes.</summary>
    /// <summary>Callback invoked when launch-at-startup changes.</summary>
    private readonly Action<bool> _applyLaunchAtStartup;

    /// <summary>Callback invoked when background connection sampling settings change.</summary>
    private readonly Action _restartConnectionSampling;

    /// <summary>Localization resolver used by bindable settings labels.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Proxy information snapshot provider used by the proxy information card.</summary>
    private readonly Func<SettingsProxyInformation> _getProxyInformation;

    /// <summary>Connection-test HTTP probe.</summary>
    private readonly Func<Uri, CancellationToken, Task<int>> _testConnectionAsync;

    /// <summary>Callback invoked when a connection-test target times out.</summary>
    private readonly Action<string> _notifyConnectionTestTimeout;

    /// <summary>Runtime log sink used for settings actions that produce diagnostics.</summary>
    private readonly Action<string, string, string, string?> _appendLog;

    /// <summary>Callback that resets persisted settings.</summary>
    private readonly Action _resetAllSettings;

    /// <summary>Callback that clears all local application data.</summary>
    private readonly Action _clearAllData;

    private readonly Action _exitApplication;

    private readonly Action _restartApplication;

    /// <summary>Startup conflict checker.</summary>
    private readonly Func<int, IReadOnlyList<StartupConflictIssue>> _checkStartupConflicts;

    /// <summary>Compares desired accent color settings against the currently applied app accent state.</summary>
    private readonly Func<AppAccentColorMode, string, bool> _isAccentColorRestartPending;

    /// <summary>Diagnostics command router used by Windows-native diagnostic buttons.</summary>
    private readonly SettingsDiagnosticsViewModel? _diagnosticsViewModel;

    /// <summary>Mihomo service controller used by transparent proxy settings.</summary>
    private readonly IMihomoServiceController _mihomoServiceController;

    /// <summary>Initializes a new settings view model.</summary>
    /// <param name="settings">Settings store. Must not be null.</param>
    /// <param name="applyLanguage">Callback used to update the active UI language. Must not be null.</param>
    /// <param name="restartConnectionSampling">Callback used to restart connection sampling. Must not be null.</param>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling)
        : this(settings, applyLanguage, _ => { }, restartConnectionSampling, _ => { }, key => key, () => new SettingsProxyInformation(string.Empty, false, string.Empty))
    {
    }

    /// <summary>Initializes a new settings view model with language and theme callbacks.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action<AppThemeMode> applyTheme,
        Action restartConnectionSampling)
        : this(settings, applyLanguage, applyTheme, restartConnectionSampling, _ => { }, key => key, () => new SettingsProxyInformation(string.Empty, false, string.Empty))
    {
    }

    /// <summary>Initializes a new settings view model with an explicit mihomo service controller.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling,
        IMihomoServiceController mihomoServiceController)
        : this(settings, applyLanguage, _ => { }, restartConnectionSampling, _ => { }, key => key, () => new SettingsProxyInformation(string.Empty, false, string.Empty), null, mihomoServiceController)
    {
    }

    /// <summary>Initializes a new settings view model with a localization resolver.</summary>
    /// <param name="settings">Settings store. Must not be null.</param>
    /// <param name="applyLanguage">Callback used to update the active UI language. Must not be null.</param>
    /// <param name="restartConnectionSampling">Callback used to restart connection sampling. Must not be null.</param>
    /// <param name="getString">Localization resolver. Must not be null.</param>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling,
        Func<string, string> getString)
        : this(settings, applyLanguage, _ => { }, restartConnectionSampling, _ => { }, getString, () => new SettingsProxyInformation(string.Empty, false, string.Empty))
    {
    }

    /// <summary>Initializes a new settings view model with launch-at-startup callback.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action<AppThemeMode> applyTheme,
        Action restartConnectionSampling,
        Action<bool> applyLaunchAtStartup)
        : this(settings, applyLanguage, applyTheme, restartConnectionSampling, applyLaunchAtStartup, key => key, () => new SettingsProxyInformation(string.Empty, false, string.Empty))
    {
    }

    /// <summary>Initializes a new settings view model with localization and proxy information providers.</summary>
    /// <param name="settings">Settings store. Must not be null.</param>
    /// <param name="applyLanguage">Callback used to update the active UI language. Must not be null.</param>
    /// <param name="restartConnectionSampling">Callback used to restart connection sampling. Must not be null.</param>
    /// <param name="getString">Localization resolver. Must not be null.</param>
    /// <param name="getProxyInformation">Proxy information provider. Must not be null.</param>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling,
        Func<string, string> getString,
        Func<SettingsProxyInformation> getProxyInformation,
        SettingsDiagnosticsViewModel? diagnosticsViewModel = null)
        : this(settings, applyLanguage, _ => { }, restartConnectionSampling, _ => { }, getString, getProxyInformation, diagnosticsViewModel)
    {
    }

    /// <summary>Initializes a new settings view model with localization, theme, startup, and proxy information providers.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action<AppThemeMode> applyTheme,
        Action restartConnectionSampling,
        Action<bool> applyLaunchAtStartup,
        Func<string, string> getString,
        Func<SettingsProxyInformation> getProxyInformation,
        SettingsDiagnosticsViewModel? diagnosticsViewModel = null,
        IMihomoServiceController? mihomoServiceController = null,
        Action<AppAccentColorMode, string>? applyAccentColor = null,
        Func<Uri, CancellationToken, Task<int>>? testConnectionAsync = null,
        Action? resetAllSettings = null,
        Action? clearAllData = null,
        Func<int, IReadOnlyList<StartupConflictIssue>>? checkStartupConflicts = null,
        Func<AppAccentColorMode, string, bool>? isAccentColorRestartPending = null,
        Action<string>? notifyConnectionTestTimeout = null,
        Action<string, string, string, string?>? appendLog = null)
        : this(
            settings,
            applyLanguage,
            applyTheme,
            restartConnectionSampling,
            applyLaunchAtStartup,
            getString,
            getProxyInformation,
            diagnosticsViewModel,
            mihomoServiceController,
            applyAccentColor,
            testConnectionAsync,
            resetAllSettings,
            clearAllData,
            checkStartupConflicts,
            isAccentColorRestartPending,
            notifyConnectionTestTimeout,
            appendLog,
            null,
            null)
    {
    }

    /// <summary>Initializes a new settings view model with localization, theme, startup, lifecycle, and proxy information providers.</summary>
    private SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action<AppThemeMode> applyTheme,
        Action restartConnectionSampling,
        Action<bool> applyLaunchAtStartup,
        Func<string, string> getString,
        Func<SettingsProxyInformation> getProxyInformation,
        SettingsDiagnosticsViewModel? diagnosticsViewModel,
        IMihomoServiceController? mihomoServiceController,
        Action<AppAccentColorMode, string>? applyAccentColor,
        Func<Uri, CancellationToken, Task<int>>? testConnectionAsync,
        Action? resetAllSettings,
        Action? clearAllData,
        Func<int, IReadOnlyList<StartupConflictIssue>>? checkStartupConflicts,
        Func<AppAccentColorMode, string, bool>? isAccentColorRestartPending,
        Action<string>? notifyConnectionTestTimeout,
        Action<string, string, string, string?>? appendLog,
        Action? exitApplication,
        Action? restartApplication)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _applyLanguage = applyLanguage ?? throw new ArgumentNullException(nameof(applyLanguage));
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        _applyLaunchAtStartup = applyLaunchAtStartup ?? throw new ArgumentNullException(nameof(applyLaunchAtStartup));
        _restartConnectionSampling = restartConnectionSampling ?? throw new ArgumentNullException(nameof(restartConnectionSampling));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _getProxyInformation = getProxyInformation ?? throw new ArgumentNullException(nameof(getProxyInformation));
        _testConnectionAsync = testConnectionAsync ?? TestConnectionAsync;
        _notifyConnectionTestTimeout = notifyConnectionTestTimeout ?? (_ => { });
        _appendLog = appendLog ?? ((_, _, _, _) => { });
        _resetAllSettings = resetAllSettings ?? (() => { });
        _clearAllData = clearAllData ?? (() => { });
        _exitApplication = exitApplication ?? ApplicationLifecycleService.Instance.ExitApplication;
        _restartApplication = restartApplication ?? ApplicationLifecycleService.Instance.RestartApplication;
        _checkStartupConflicts = checkStartupConflicts ?? (_ => []);
        _isAccentColorRestartPending = isAccentColorRestartPending ?? IsAccentColorChangedSinceLoad;
        _diagnosticsViewModel = diagnosticsViewModel;
        _mihomoServiceController = mihomoServiceController ?? AlwaysAvailableMihomoServiceController.Instance;
        RefreshSelectorOptions();
        WindowsDiagnosticCommand = new AsyncRelayCommand(ExecuteWindowsDiagnosticCommandAsync);
        DeployMihomoServiceCommand = new AsyncRelayCommand(DeployMihomoServiceAsync);
        UninstallMihomoServiceCommand = new AsyncRelayCommand(UninstallMihomoServiceAsync);
        ExitApplicationCommand = new RelayCommand(ExitApplication);
        RestartApplicationCommand = new RelayCommand(RestartApplication);
        Load();
        ResetDiagnosticStatusText();
    }

    public string PageTitleText => _getString("Nav.Settings");

    public string DescriptionText => _getString("Page.Settings.Description");

    public string LanguageSectionTitleText => _getString("Settings.Section.Language");

    public string LanguageTitleText => _getString("Settings.Language.Title");

    public string LanguageDescriptionText => _getString("Settings.Language.Description");

    public IReadOnlyList<string> DisplayLanguageOptions => _displayLanguageOptions;

    public string AppThemeModeTitleText => _getString("Settings.AppTheme.Title");

    public string AppThemeModeDescriptionText => _getString("Settings.AppTheme.Description");

    public string AppThemeFollowSystemText => _getString("Settings.AppTheme.FollowSystem");

    public string AppThemeLightText => _getString("Settings.AppTheme.Light");

    public string AppThemeDarkText => _getString("Settings.AppTheme.Dark");

    public IReadOnlyList<string> AppThemeModeOptions => _appThemeModeOptions;

    public string AppAccentColorTitleText => IsAppAccentColorRestartPending
        ? $"{_getString("Settings.AppAccentColor.Title")}*"
        : _getString("Settings.AppAccentColor.Title");

    public string AppAccentColorDescriptionText => _getString("Settings.AppAccentColor.Description");

    public string AppAccentColorFollowSystemText => _getString("Settings.AppAccentColor.FollowSystem");

    public string AppAccentColorCustomText => _getString("Settings.AppAccentColor.Custom");

    public string AppAccentColorPickText => _getString("Settings.AppAccentColor.Pick");

    public IReadOnlyList<string> AppAccentColorModeOptions => _appAccentColorModeOptions;

    public string LaunchAtStartupTitleText => _getString("Settings.LaunchAtStartup.Title");

    public string LaunchAtStartupDescriptionText => _getString("Settings.LaunchAtStartup.Description");

    public string StartupSectionTitleText => _getString("Settings.Section.Startup");

    public string CheckStartupConflictsTitleText => _getString("Settings.CheckStartupConflicts.Title");

    public string CheckStartupConflictsDescriptionText => _getString("Settings.CheckStartupConflicts.Description");

    public string CheckStartupConflictsNowText => _getString("Settings.CheckStartupConflicts.Now");

    public string StartupGuideTitleText => _getString("Settings.StartupGuide.Title");

    public string StartupGuideDescriptionText => _getString("Settings.StartupGuide.Description");

    public string StartupGuideShowNowText => _getString("Settings.StartupGuide.ShowNow");

    public string StartupRestoreFallbackTitleText => _getString("Settings.StartupRestoreFallback.Title");

    public string StartupRestoreFallbackDescriptionText => _getString("Settings.StartupRestoreFallback.Description");

    public string StartupRestoreFallbackStatusText
    {
        get => _startupRestoreFallbackStatusText;
        private set => SetProperty(ref _startupRestoreFallbackStatusText, value);
    }

    public string RegisterText => _getString("Command.Register");

    public string DetectText => _getString("Command.Detect");

    public string ProxySectionTitleText => _getString("Settings.Section.Proxy");

    public string TransparentProxyTitleText => _getString("Settings.TransparentProxy.Title");

    public string TransparentProxyDescriptionText => _getString("Settings.TransparentProxy.Description");

    public string TransparentProxyServiceTitleText => _getString("Settings.TransparentProxy.Service.Title");

    public string TransparentProxyServiceDescriptionText => _getString("Settings.TransparentProxy.Service.Description");

    public string DeployMihomoServiceText => _getString("Command.Deploy");

    public string UninstallMihomoServiceText => _getString("Command.Uninstall");

    public string MixedPortTitleText => _getString("Settings.MixedPort.Title");

    public string MixedPortDescriptionText => _getString("Settings.MixedPort.Description");

    public string ConnectionTestUrlTitleText => _getString("Settings.ConnectionTestUrl.Title");

    public string ConnectionTestUrlDescriptionText => _getString("Settings.ConnectionTestUrl.Description");

    public string ConnectionTestProxyUrl1TitleText => _getString("Settings.ConnectionTestUrl.Proxy1");

    public string ConnectionTestProxyUrl2TitleText => _getString("Settings.ConnectionTestUrl.Proxy2");

    public string ConnectionTestDirectUrlTitleText => _getString("Settings.ConnectionTestUrl.Direct");

    public string ConnectionTestStatusColumnText => _getString("Settings.ConnectionTest.StatusColumn");

    public string ConnectionTestLatencyColumnText => _getString("Settings.ConnectionTest.LatencyColumn");

    public string ConnectionTestUrlSummaryText => string.Join(
        " | ",
        FormatConnectionTestUrlSummaryPart(ConnectionTestProxyUrl1),
        FormatConnectionTestUrlSummaryPart(ConnectionTestProxyUrl2),
        FormatConnectionTestUrlSummaryPart(ConnectionTestDirectUrl));

    public bool IsConnectionTestRunning
    {
        get => _isConnectionTestRunning;
        private set => SetProperty(ref _isConnectionTestRunning, value);
    }

    public string ProxyInformationTitleText => _getString("Settings.ProxyInformation.Title");

    public string ProxyInformationDescriptionText => _getString("Settings.ProxyInformation.Description");

    public string ProxyLocalEntryText
    {
        get => _proxyLocalEntryText;
        private set => SetProperty(ref _proxyLocalEntryText, value);
    }

    public string ProxyCoreConfigurationText
    {
        get => _proxyCoreConfigurationText;
        private set => SetProperty(ref _proxyCoreConfigurationText, value);
    }

    public string ProxyCoreBinaryText
    {
        get => _proxyCoreBinaryText;
        private set => SetProperty(ref _proxyCoreBinaryText, value);
    }

    public string ConnectionSamplingTitleText => _getString("Settings.ConnectionSampling.Title");

    public string ConnectionSamplingDescriptionText => _getString("Settings.ConnectionSampling.Description");

    public string SamplingIntervalTitleText => _getString("Settings.SamplingInterval.Title");

    public string SamplingIntervalDescriptionText => _getString("Settings.SamplingInterval.Description");

    public string StartupConflictCheckTitleText => _getString("Settings.StartupConflictCheck.Title");

    public string StartupConflictCheckDescriptionText => _getString("Settings.StartupConflictCheck.Description");

    public string StartupBehaviorModeTitleText => _getString("Settings.StartupBehavior.Title");

    public string StartupBehaviorModeDescriptionText => _getString("Settings.StartupBehavior.Description");

    public string StartupBehaviorLastSettingText => _getString("Settings.StartupBehavior.LastSetting");

    public string StartupBehaviorStartRuleProxyText => _getString("Settings.StartupBehavior.StartRuleProxy");

    public string StartupBehaviorDisableProxyText => _getString("Settings.StartupBehavior.DisableProxy");

    public IReadOnlyList<string> StartupBehaviorModeOptions => _startupBehaviorModeOptions;

    public string TriggerSectionTitleText => _getString("Settings.Section.Triggers");

    public string TriggersEnabledTitleText => IsTriggerEngineRestartPending
        ? $"{_getString("Settings.Triggers.Enabled.Title")}*"
        : _getString("Settings.Triggers.Enabled.Title");

    public string TriggersEnabledDescriptionText => _getString("Settings.Triggers.Enabled.Description");

    public string TriggerNotificationsEnabledTitleText => _getString("Settings.Triggers.Notifications.Title");

    public string TriggerNotificationsEnabledDescriptionText => _getString("Settings.Triggers.Notifications.Description");

    public string TraySectionTitleText => _getString("Settings.Section.Tray");

    public string CloseBehaviorModeTitleText => _getString("Settings.CloseBehavior.Title");

    public string CloseBehaviorModeDescriptionText => _getString("Settings.CloseBehavior.Description");

    public string CloseBehaviorExitWithoutConfirmationText => _getString("Settings.CloseBehavior.ExitWithoutConfirmation");

    public string CloseBehaviorConfirmExitText => _getString("Settings.CloseBehavior.ConfirmExit");

    public string CloseBehaviorMinimizeToTrayText => _getString("Settings.CloseBehavior.MinimizeToTray");

    public IReadOnlyList<string> CloseBehaviorModeOptions => _closeBehaviorModeOptions;

    public string TrayUseMonochromeInactiveIconTitleText => IsTrayIconRestartPending
        ? $"{_getString("Settings.Tray.MonochromeInactiveIcon.Title")}*"
        : _getString("Settings.Tray.MonochromeInactiveIcon.Title");

    public string TrayUseMonochromeInactiveIconDescriptionText => _getString("Settings.Tray.MonochromeInactiveIcon.Description");

    public string TrayVisibleFeaturesTitleText => _getString("Settings.Tray.VisibleFeatures.Title");

    public string TrayVisibleFeaturesDescriptionText => _getString("Settings.Tray.VisibleFeatures.Description");

    public string TrayVisibleFeatureSummaryText => string.Format(
        _getString("Settings.Tray.VisibleFeatures.Summary.Format"),
        GetTrayVisibleFeatureDefinitions().Count);

    public string TrayVisibleFeatureSearchPlaceholderText => _getString("Settings.Tray.VisibleFeatures.SearchPlaceholder");

    public string ResetGroupServiceDeploymentNoteText => _getString("Settings.ResetGroupConfirm.ServiceDeploymentNote");

    public string WindowsNativeSectionTitleText => _getString("Settings.Section.WindowsNative");

    public string WindowsNativeTitleText => _getString("Settings.WindowsNative.Title");

    public string WindowsNativeDescriptionText => _getString("Settings.WindowsNative.Description");

    public string OpenText => _getString("Command.Open");

    public string EditText => _getString("Command.Edit");

    public string ExportText => _getString("Command.Export");

    public string ImportText => _getString("Command.Import");

    public string CheckText => _getString("Command.Check");

    public string TestText => _getString("Command.Test");

    public string WslDiagnosticTitleText => _getString("Settings.Wsl.Title");

    public string TerminalDiagnosticTitleText => _getString("Settings.Terminal.Title");

    public string StoreDiagnosticTitleText => _getString("Settings.Store.Title");

    public string DiagnoseText => _getString("Command.Diagnose");

    public string ApplyText => _getString("Command.Apply");

    public string ResetText => _getString("Command.Reset");

    public string CleanupText => _getString("Command.Cleanup");

    public string ExitApplicationText => _getString("Command.CloseApplication");

    public string RestartApplicationText => _getString("Command.RestartApplication");

    public string ApplicationLifecycleTitleText => _getString("Settings.ApplicationLifecycle.Title");

    public string ApplicationLifecycleDescriptionText => _getString("Settings.ApplicationLifecycle.Description");

    public string DiagnosticNotRunText => _getString("Diagnostic.NotRun");

    public string WslDiagnosticStatusText
    {
        get => _wslDiagnosticStatusText;
        private set => SetProperty(ref _wslDiagnosticStatusText, value);
    }

    public string TerminalDiagnosticStatusText
    {
        get => _terminalDiagnosticStatusText;
        private set => SetProperty(ref _terminalDiagnosticStatusText, value);
    }

    public string StoreDiagnosticStatusText
    {
        get => _storeDiagnosticStatusText;
        private set => SetProperty(ref _storeDiagnosticStatusText, value);
    }

    public string CheckStaleProxyTitleText => _getString("Settings.CheckStaleProxy.Title");

    public string CheckStaleProxyDescriptionText => _getString("Settings.CheckStaleProxy.Description");

    public string RestoreProxyOnExitTitleText => _getString("Settings.RestoreProxyOnExit.Title");

    public string RestoreProxyOnExitDescriptionText => _getString("Settings.RestoreProxyOnExit.Description");

    public string MainlandChinaSectionTitleText => _getString("Settings.Section.MainlandChina");

    public string MainlandChinaDisplayTitleText => IsMainlandChinaDisplayRestartPending
        ? _getString("Settings.MainlandChinaDisplay.Title") + "*"
        : _getString("Settings.MainlandChinaDisplay.Title");

    public string MainlandChinaDisplayDescriptionText => _getString("Settings.MainlandChinaDisplay.Description");

    public string MainlandChinaDisabledText => _getString("Settings.MainlandChinaFeature.Disabled");

    public string MainlandChinaFlagOnlyText => _getString("Settings.MainlandChinaFeature.FlagOnly");

    public string MainlandChinaFlagAndTextText => _getString("Settings.MainlandChinaFeature.FlagAndText");

    public string MainlandChinaKeywordFilterText => _getString("Settings.MainlandChinaFeature.KeywordFilter");

    public string MainlandChinaAllText => _getString("Settings.MainlandChinaFeature.All");

    public IReadOnlyList<string> MainlandChinaFeatureModeOptions => _mainlandChinaFeatureModeOptions;

    public string MainlandChinaUrlBlockingTitleText => IsMainlandChinaDisplayRestartPending
        ? _getString("Settings.MainlandChinaUrlBlocking.Title") + "*"
        : _getString("Settings.MainlandChinaUrlBlocking.Title");

    public string MainlandChinaUrlBlockingDescriptionText => _getString("Settings.MainlandChinaUrlBlocking.Description");

    public string NotificationSectionTitleText => _getString("Settings.Section.Notification");

    public string NotificationEnabledTitleText => _getString("Settings.Notification.Enabled.Title");

    public string NotificationEnabledDescriptionText => _getString("Settings.Notification.Enabled.Description");

    public string NotificationTitleText => _getString("Settings.Notification.Title");

    public string NotificationDescriptionText => _getString("Settings.Notification.Description");

    public string NotificationDefaultText => _getString("Settings.Notification.Default");

    public string NotificationCriticalOnlyText => _getString("Settings.Notification.CriticalOnly");

    public string NotificationMoreText => _getString("Settings.Notification.More");

    public IReadOnlyList<string> NotificationLevelOptions => _notificationLevelOptions;

    public string DataSectionTitleText => _getString("Settings.Section.Data");

    public string DataPackageTitleText => BackupRestoreTitleText;

    public string DataPackageDescriptionText => BackupRestoreDescriptionText;

    public string BackupRestoreTitleText => _getString("Settings.BackupRestore.Title");

    public string BackupRestoreDescriptionText => _getString("Settings.BackupRestore.Description");

    public string DataExportTitleText => _getString("Settings.DataExport.Title");

    public string DataExportDescriptionText => _getString("Settings.DataExport.Description");

    public string DataPackageScopeSettingsText => _getString("Settings.DataPackage.Scope.Settings");

    public string DataPackageScopeSettingsAndProxyConfigurationText => _getString("Settings.DataPackage.Scope.SettingsAndProxyConfiguration");

    public string ResetAllSettingsTitleText => _getString("Settings.ResetAllSettings.Title");

    public string ResetAllSettingsDescriptionText => _getString("Settings.ResetAllSettings.Description");

    public string ClearAllDataTitleText => _getString("Settings.ClearAllData.Title");

    public string ClearAllDataDescriptionText => _getString("Settings.ClearAllData.Description");

    public string ResetGroupToDefaultsText => _getString("Settings.ResetGroupToDefaults");

    public string ResetGroupConfirmTitleText => _getString("Settings.ResetGroupConfirm.Title");

    public string ResetGroupConfirmMessageText => _getString("Settings.ResetGroupConfirm.Message");

    /// <summary>Backing field for <see cref="DisplayLanguage"/>.</summary>
    private AppLanguage _displayLanguage;

    /// <summary>Display language loaded when this view model was initialized.</summary>
    private AppLanguage _loadedDisplayLanguage;

    /// <summary>Backing field for <see cref="AppThemeMode"/>.</summary>
    private AppThemeMode _appThemeMode;

    /// <summary>Backing field for <see cref="AppAccentColorMode"/>.</summary>
    private AppAccentColorMode _appAccentColorMode;

    /// <summary>Backing field for <see cref="AppAccentColorValue"/>.</summary>
    private string _appAccentColorValue = string.Empty;

    /// <summary>Accent color mode loaded when this view model was initialized.</summary>
    private AppAccentColorMode _loadedAppAccentColorMode;

    /// <summary>Accent color value loaded when this view model was initialized.</summary>
    private string _loadedAppAccentColorValue = string.Empty;

    /// <summary>Mainland China feature mode loaded when this view model was initialized.</summary>
    private MainlandChinaFeatureMode _loadedMainlandChinaFeatureMode;

    /// <summary>Mainland China URL blocking value loaded when this view model was initialized.</summary>
    private bool _loadedMainlandChinaUrlBlockingEnabled;

    /// <summary>Stable display-language option source used by WinUI ComboBox.</summary>
    private readonly ObservableCollection<string> _displayLanguageOptions = [];

    /// <summary>Stable app-theme option source used by WinUI ComboBox.</summary>
    private readonly ObservableCollection<string> _appThemeModeOptions = [];

    /// <summary>Stable accent-color option source used by WinUI ComboBox.</summary>
    private readonly ObservableCollection<string> _appAccentColorModeOptions = [];

    /// <summary>Stable startup-behavior option source used by WinUI ComboBox.</summary>
    private readonly ObservableCollection<string> _startupBehaviorModeOptions = [];

    /// <summary>Stable close-behavior option source used by WinUI ComboBox.</summary>
    private readonly ObservableCollection<string> _closeBehaviorModeOptions = [];

    /// <summary>Stable mainland-China feature option source used by WinUI ComboBox.</summary>
    private readonly ObservableCollection<string> _mainlandChinaFeatureModeOptions = [];

    /// <summary>Stable notification-level option source used by WinUI ComboBox.</summary>
    private readonly ObservableCollection<string> _notificationLevelOptions = [];

    /// <summary>Backing field for <see cref="LaunchAtStartupEnabled"/>.</summary>
    private bool _launchAtStartupEnabled;

    /// <summary>Backing field for <see cref="TransparentProxyEnabled"/>.</summary>
    private bool _transparentProxyEnabled;

    /// <summary>Backing field for <see cref="StartupRestoreFallbackStatusText"/>.</summary>
    private string _startupRestoreFallbackStatusText = string.Empty;

    /// <summary>Backing field for <see cref="MihomoServiceStatusText"/>.</summary>
    private string _mihomoServiceStatusText = string.Empty;

    /// <summary>Backing field for <see cref="NotificationLevel"/>.</summary>
    private NotificationLevel _notificationLevel;

    /// <summary>Backing field for <see cref="NotificationEnabled"/>.</summary>
    private bool _notificationEnabled;

    /// <summary>Latest mihomo service status snapshot.</summary>
    private MihomoServiceStatus _mihomoServiceStatus;

    /// <summary>Backing field for <see cref="MixedPort"/>.</summary>
    private int _mixedPort;

    /// <summary>Backing field for <see cref="ConnectionSamplingEnabled"/>.</summary>
    private bool _connectionSamplingEnabled;

    /// <summary>Backing field for <see cref="ConnectionSamplingIntervalSeconds"/>.</summary>
    private int _connectionSamplingIntervalSeconds;

    /// <summary>Backing field for <see cref="StartupConflictCheckEnabled"/>.</summary>
    private bool _startupConflictCheckEnabled;

    /// <summary>Backing field for <see cref="StartupBehaviorMode"/>.</summary>
    private StartupBehaviorMode _startupBehaviorMode;

    /// <summary>Backing field for <see cref="ShowStartupGuideOnStartup"/>.</summary>
    private bool _showStartupGuideOnStartup;

    /// <summary>Backing field for <see cref="TriggersEnabled"/>.</summary>
    private bool _triggersEnabled;

    /// <summary>Trigger engine setting loaded when this view model was initialized.</summary>
    private bool _loadedTriggersEnabled;

    /// <summary>Backing field for <see cref="TriggerNotificationsEnabled"/>.</summary>
    private bool _triggerNotificationsEnabled;

    /// <summary>Backing field for <see cref="CloseBehaviorMode"/>.</summary>
    private CloseBehaviorMode _closeBehaviorMode;

    /// <summary>Backing field for <see cref="TrayUseMonochromeInactiveIcon"/>.</summary>
    private bool _trayUseMonochromeInactiveIcon;

    /// <summary>Tray monochrome icon setting loaded when this view model was initialized.</summary>
    private bool _loadedTrayUseMonochromeInactiveIcon;

    /// <summary>Backing field for <see cref="TrayVisibleFeatureIds"/>.</summary>
    private string _trayVisibleFeatureIds = DefaultTrayVisibleFeatureIds;

    /// <summary>Backing field for <see cref="CheckStaleProxyOnStartup"/>.</summary>
    private bool _checkStaleProxyOnStartup;

    /// <summary>Backing field for <see cref="RestoreProxyOnExit"/>.</summary>
    private bool _restoreProxyOnExit;

    /// <summary>Backing field for <see cref="MainlandChinaFeatureMode"/>.</summary>
    private MainlandChinaFeatureMode _mainlandChinaFeatureMode;

    /// <summary>Backing field for <see cref="MainlandChinaUrlBlockingEnabled"/>.</summary>
    private bool _mainlandChinaUrlBlockingEnabled;

    /// <summary>Backing field for <see cref="ConnectionTestUrl"/>.</summary>
    private string _connectionTestUrl = string.Empty;

    private string _connectionTestProxyUrl1 = string.Empty;

    private string _connectionTestProxyUrl2 = string.Empty;

    private string _connectionTestDirectUrl = string.Empty;

    /// <summary>Backing field for <see cref="IsConnectionTestRunning"/>.</summary>
    private bool _isConnectionTestRunning;

    /// <summary>Backing field for <see cref="ProxyLocalEntryText"/>.</summary>
    private string _proxyLocalEntryText = string.Empty;

    /// <summary>Backing field for <see cref="ProxyCoreConfigurationText"/>.</summary>
    private string _proxyCoreConfigurationText = string.Empty;

    /// <summary>Backing field for <see cref="ProxyCoreBinaryText"/>.</summary>
    private string _proxyCoreBinaryText = string.Empty;

    /// <summary>Backing field for <see cref="WslDiagnosticStatusText"/>.</summary>
    private string _wslDiagnosticStatusText = string.Empty;

    /// <summary>Backing field for <see cref="TerminalDiagnosticStatusText"/>.</summary>
    private string _terminalDiagnosticStatusText = string.Empty;

    /// <summary>Backing field for <see cref="StoreDiagnosticStatusText"/>.</summary>
    private string _storeDiagnosticStatusText = string.Empty;

    public AsyncRelayCommand WindowsDiagnosticCommand { get; }

    public AsyncRelayCommand DeployMihomoServiceCommand { get; }

    public AsyncRelayCommand UninstallMihomoServiceCommand { get; }

    public RelayCommand ExitApplicationCommand { get; }

    public RelayCommand RestartApplicationCommand { get; }

    public AppLanguage DisplayLanguage
    {
        get => _displayLanguage;
        private set
        {
            if (SetProperty(ref _displayLanguage, value))
            {
                OnPropertyChanged(nameof(DisplayLanguageIndex));
            }
        }
    }

    public int DisplayLanguageIndex
    {
        get => DisplayLanguage == AppLanguage.AutoDetect ? 0 : (int)DisplayLanguage + 1;
        set => SetDisplayLanguageIndex(value);
    }

    public AppThemeMode AppThemeMode
    {
        get => _appThemeMode;
        private set
        {
            if (SetProperty(ref _appThemeMode, value))
            {
                OnPropertyChanged(nameof(AppThemeModeIndex));
            }
        }
    }

    public int AppThemeModeIndex
    {
        get => (int)AppThemeMode;
        set => SetAppThemeModeIndex(value);
    }

    public AppAccentColorMode AppAccentColorMode
    {
        get => _appAccentColorMode;
        private set
        {
            if (SetProperty(ref _appAccentColorMode, value))
            {
                OnPropertyChanged(nameof(AppAccentColorModeIndex));
                OnPropertyChanged(nameof(IsCustomAccentColorSelected));
                RaiseAppAccentColorRestartStateChanged();
            }
        }
    }

    public int AppAccentColorModeIndex
    {
        get => (int)AppAccentColorMode;
        set => SetAppAccentColorModeIndex(value);
    }

    public string AppAccentColorValue
    {
        get => _appAccentColorValue;
        private set
        {
            if (SetProperty(ref _appAccentColorValue, value))
            {
                RaiseAppAccentColorRestartStateChanged();
            }
        }
    }

    public bool IsCustomAccentColorSelected => AppAccentColorMode == ClashSharp.Model.AppAccentColorMode.Custom;

    public bool IsAppAccentColorRestartPending => _isAccentColorRestartPending(AppAccentColorMode, AppAccentColorValue);

    public bool IsDisplayLanguageRestartPending => DisplayLanguage != _loadedDisplayLanguage;

    public bool IsMainlandChinaDisplayRestartPending =>
        MainlandChinaFeatureMode != _loadedMainlandChinaFeatureMode
        || MainlandChinaUrlBlockingEnabled != _loadedMainlandChinaUrlBlockingEnabled;

    public bool IsTriggerEngineRestartPending => false;

    public bool IsTrayIconRestartPending => TrayUseMonochromeInactiveIcon != _loadedTrayUseMonochromeInactiveIcon;

    public bool HasRestartRequiredSettings =>
        IsDisplayLanguageRestartPending
        || IsAppAccentColorRestartPending
        || IsMainlandChinaDisplayRestartPending
        || IsTrayIconRestartPending;

    public string RestartRequiredNoticeText => _getString("Settings.RestartRequiredNotice");

    public string RestartRequiredTitleText => _getString("Settings.RestartRequired.Title");

    public bool LaunchAtStartupEnabled
    {
        get => _launchAtStartupEnabled;
        set => SetLaunchAtStartupEnabled(value);
    }

    public bool TransparentProxyEnabled
    {
        get => _transparentProxyEnabled;
        set => SetTransparentProxyEnabled(value);
    }

    public bool CanToggleTransparentProxy => _mihomoServiceStatus.IsInstalled;

    public string MihomoServiceStatusText
    {
        get => _mihomoServiceStatusText;
        private set => SetProperty(ref _mihomoServiceStatusText, value);
    }

    public int MixedPort
    {
        get => _mixedPort;
        private set
        {
            if (SetProperty(ref _mixedPort, value))
            {
                OnPropertyChanged(nameof(MixedPortValue));
            }
        }
    }

    public double MixedPortValue
    {
        get => MixedPort;
        set => SetMixedPort(value);
    }

    public bool ConnectionSamplingEnabled
    {
        get => _connectionSamplingEnabled;
        set => SetConnectionSamplingEnabled(value);
    }

    public int ConnectionSamplingIntervalSeconds
    {
        get => _connectionSamplingIntervalSeconds;
        private set
        {
            if (SetProperty(ref _connectionSamplingIntervalSeconds, value))
            {
                OnPropertyChanged(nameof(ConnectionSamplingIntervalSecondsValue));
            }
        }
    }

    public bool StartupConflictCheckEnabled
    {
        get => _startupConflictCheckEnabled;
        set => SetStartupConflictCheckEnabled(value);
    }

    public StartupBehaviorMode StartupBehaviorMode
    {
        get => _startupBehaviorMode;
        private set
        {
            if (SetProperty(ref _startupBehaviorMode, value))
            {
                OnPropertyChanged(nameof(StartupBehaviorModeIndex));
            }
        }
    }

    public int StartupBehaviorModeIndex
    {
        get => (int)StartupBehaviorMode;
        set => SetStartupBehaviorModeIndex(value);
    }

    public bool ShowStartupGuideOnStartup
    {
        get => _showStartupGuideOnStartup;
        set => SetShowStartupGuideOnStartup(value);
    }

    public bool TriggersEnabled
    {
        get => _triggersEnabled;
        set => SetTriggersEnabled(value);
    }

    public bool TriggerNotificationsEnabled
    {
        get => _triggerNotificationsEnabled;
        set => SetTriggerNotificationsEnabled(value);
    }

    public CloseBehaviorMode CloseBehaviorMode
    {
        get => _closeBehaviorMode;
        private set
        {
            if (SetProperty(ref _closeBehaviorMode, value))
            {
                OnPropertyChanged(nameof(CloseBehaviorModeIndex));
            }
        }
    }

    public int CloseBehaviorModeIndex
    {
        get => (int)CloseBehaviorMode;
        set => SetCloseBehaviorModeIndex(value);
    }

    public bool TrayUseMonochromeInactiveIcon
    {
        get => _trayUseMonochromeInactiveIcon;
        set => SetTrayUseMonochromeInactiveIcon(value);
    }

    public string TrayVisibleFeatureIds
    {
        get => _trayVisibleFeatureIds;
        private set
        {
            if (SetProperty(ref _trayVisibleFeatureIds, value))
            {
                OnPropertyChanged(nameof(TrayVisibleFeatureSummaryText));
            }
        }
    }

    public double ConnectionSamplingIntervalSecondsValue
    {
        get => ConnectionSamplingIntervalSeconds;
        set => SetConnectionSamplingIntervalSeconds(value);
    }

    public bool CheckStaleProxyOnStartup
    {
        get => _checkStaleProxyOnStartup;
        set => SetCheckStaleProxyOnStartup(value);
    }

    public bool RestoreProxyOnExit
    {
        get => _restoreProxyOnExit;
        set => SetRestoreProxyOnExit(value);
    }

    public MainlandChinaFeatureMode MainlandChinaFeatureMode
    {
        get => _mainlandChinaFeatureMode;
        private set
        {
            if (SetProperty(ref _mainlandChinaFeatureMode, value))
            {
                OnPropertyChanged(nameof(MainlandChinaFeatureModeIndex));
                RaiseMainlandChinaRestartStateChanged();
            }
        }
    }

    public int MainlandChinaFeatureModeIndex
    {
        get => (int)MainlandChinaFeatureMode;
        set => SetMainlandChinaFeatureModeIndex(value);
    }

    public bool MainlandChinaUrlBlockingEnabled
    {
        get => _mainlandChinaUrlBlockingEnabled;
        set => SetMainlandChinaUrlBlockingEnabled(value);
    }

    public bool NotificationEnabled
    {
        get => _notificationEnabled;
        set => SetNotificationEnabled(value);
    }

    public NotificationLevel NotificationLevel
    {
        get => _notificationLevel;
        private set
        {
            if (SetProperty(ref _notificationLevel, value))
            {
                OnPropertyChanged(nameof(NotificationLevelIndex));
            }
        }
    }

    public int NotificationLevelIndex
    {
        get => (int)NotificationLevel;
        set => SetNotificationLevelIndex(value);
    }

    public string ConnectionTestUrl
    {
        get => _connectionTestUrl;
        private set => SetProperty(ref _connectionTestUrl, value);
    }

    public string ConnectionTestProxyUrl1
    {
        get => _connectionTestProxyUrl1;
        private set
        {
            if (SetProperty(ref _connectionTestProxyUrl1, value))
            {
                OnPropertyChanged(nameof(ConnectionTestUrlSummaryText));
            }
        }
    }

    public string ConnectionTestProxyUrl2
    {
        get => _connectionTestProxyUrl2;
        private set
        {
            if (SetProperty(ref _connectionTestProxyUrl2, value))
            {
                OnPropertyChanged(nameof(ConnectionTestUrlSummaryText));
            }
        }
    }

    public string ConnectionTestDirectUrl
    {
        get => _connectionTestDirectUrl;
        private set
        {
            if (SetProperty(ref _connectionTestDirectUrl, value))
            {
                OnPropertyChanged(nameof(ConnectionTestUrlSummaryText));
            }
        }
    }

    /// <summary>Loads the latest persisted settings into the view model properties.</summary>
    public void Load()
    {
        _loadedDisplayLanguage = _settings.DisplayLanguage;
        DisplayLanguage = _loadedDisplayLanguage;
        AppThemeMode = _settings.AppThemeMode;
        AppAccentColorMode = _settings.AppAccentColorMode;
        AppAccentColorValue = _settings.AppAccentColorValue;
        _loadedAppAccentColorMode = AppAccentColorMode;
        _loadedAppAccentColorValue = AppAccentColorValue;
        RaiseAppAccentColorRestartStateChanged();
        SetProperty(ref _launchAtStartupEnabled, _settings.LaunchAtStartupEnabled, nameof(LaunchAtStartupEnabled));
        RefreshMihomoServiceStatus();
        if (!CanToggleTransparentProxy && _settings.TransparentProxyEnabled)
        {
            _settings.TransparentProxyEnabled = false;
        }

        SetProperty(ref _transparentProxyEnabled, _settings.TransparentProxyEnabled, nameof(TransparentProxyEnabled));
        MixedPort = _settings.MixedPort;
        SetProperty(ref _connectionSamplingEnabled, _settings.ConnectionSamplingEnabled, nameof(ConnectionSamplingEnabled));
        ConnectionSamplingIntervalSeconds = _settings.ConnectionSamplingIntervalSeconds;
        SetProperty(ref _startupConflictCheckEnabled, _settings.StartupConflictCheckEnabled, nameof(StartupConflictCheckEnabled));
        StartupBehaviorMode = _settings.StartupBehaviorMode;
        SetProperty(ref _showStartupGuideOnStartup, _settings.ShowStartupGuideOnStartup, nameof(ShowStartupGuideOnStartup));
        _loadedTriggersEnabled = _settings.TriggersEnabled;
        SetProperty(ref _triggersEnabled, _loadedTriggersEnabled, nameof(TriggersEnabled));
        SetProperty(ref _triggerNotificationsEnabled, _settings.TriggerNotificationsEnabled, nameof(TriggerNotificationsEnabled));
        CloseBehaviorMode = _settings.CloseBehaviorMode;
        _loadedTrayUseMonochromeInactiveIcon = _settings.TrayUseMonochromeInactiveIcon;
        SetProperty(ref _trayUseMonochromeInactiveIcon, _loadedTrayUseMonochromeInactiveIcon, nameof(TrayUseMonochromeInactiveIcon));
        TrayVisibleFeatureIds = _settings.TrayVisibleFeatureIds;
        RaiseTriggerRestartStateChanged();
        RaiseTrayIconRestartStateChanged();
        RefreshStartupRestoreFallbackStatus();
        SetProperty(ref _checkStaleProxyOnStartup, _settings.CheckStaleProxyOnStartup, nameof(CheckStaleProxyOnStartup));
        SetProperty(ref _restoreProxyOnExit, _settings.RestoreProxyOnExit, nameof(RestoreProxyOnExit));
        _loadedMainlandChinaFeatureMode = _settings.MainlandChinaFeatureMode;
        _loadedMainlandChinaUrlBlockingEnabled = _settings.MainlandChinaUrlBlockingEnabled;
        MainlandChinaFeatureMode = _loadedMainlandChinaFeatureMode;
        SetProperty(ref _mainlandChinaUrlBlockingEnabled, _loadedMainlandChinaUrlBlockingEnabled, nameof(MainlandChinaUrlBlockingEnabled));
        RaiseMainlandChinaRestartStateChanged();
        SetProperty(ref _notificationEnabled, _settings.NotificationEnabled, nameof(NotificationEnabled));
        NotificationLevel = _settings.NotificationLevel;
        ConnectionTestUrl = _settings.ConnectionTestUrl;
        ConnectionTestProxyUrl1 = _settings.ConnectionTestProxyUrl1;
        ConnectionTestProxyUrl2 = _settings.ConnectionTestProxyUrl2;
        ConnectionTestDirectUrl = _settings.ConnectionTestDirectUrl;
        RefreshProxyInformation();
        RaiseDisplayLanguageRestartStateChanged();
    }

    /// <summary>Persists a display language selected by combo box index.</summary>
    /// <param name="index">Language enum index.</param>
    /// <returns>True when the language was valid and persisted; otherwise false.</returns>
    public bool SetDisplayLanguageIndex(int index)
    {
        AppLanguage language;
        if (index == 0)
        {
            language = AppLanguage.AutoDetect;
        }
        else
        {
            int languageValue = index - 1;
            if (!Enum.IsDefined((AppLanguage)languageValue))
            {
                return false;
            }

            language = (AppLanguage)languageValue;
            if (language == AppLanguage.AutoDetect)
            {
                return false;
            }
        }

        if (DisplayLanguage == language && _settings.DisplayLanguage == language)
        {
            return false;
        }

        _settings.DisplayLanguage = language;
        DisplayLanguage = language;
        RaiseDisplayLanguageRestartStateChanged();
        return true;
    }

    /// <summary>Persists an app theme selected by combo box index.</summary>
    /// <param name="index">Theme enum index.</param>
    /// <returns>True when the theme was valid and persisted; otherwise false.</returns>
    public bool SetAppThemeModeIndex(int index)
    {
        if (!Enum.IsDefined((AppThemeMode)index))
        {
            return false;
        }

        AppThemeMode mode = (AppThemeMode)index;
        _settings.AppThemeMode = mode;
        AppThemeMode = mode;
        _applyTheme(mode);
        return true;
    }

    /// <summary>Persists an app accent color behavior selected by combo box index.</summary>
    /// <param name="index">Accent color mode enum index.</param>
    /// <returns>True when the mode was valid and persisted; otherwise false.</returns>
    public bool SetAppAccentColorModeIndex(int index)
    {
        if (!Enum.IsDefined((AppAccentColorMode)index))
        {
            return false;
        }

        AppAccentColorMode mode = (AppAccentColorMode)index;
        _settings.AppAccentColorMode = mode;
        AppAccentColorMode = mode;
        return true;
    }

    /// <summary>Persists a custom accent color value.</summary>
    /// <param name="value">Hex color value.</param>
    /// <returns>True when the color was valid and persisted; otherwise false.</returns>
    public bool SetAppAccentColorValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            _settings.AppAccentColorValue = value;
            AppAccentColorValue = _settings.AppAccentColorValue;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Raises bindable notifications for the app accent color restart marker.</summary>
    private void RaiseAppAccentColorRestartStateChanged()
    {
        OnPropertyChanged(nameof(IsAppAccentColorRestartPending));
        OnPropertyChanged(nameof(HasRestartRequiredSettings));
        OnPropertyChanged(nameof(AppAccentColorTitleText));
        OnPropertyChanged(nameof(RestartRequiredTitleText));
        OnPropertyChanged(nameof(RestartRequiredNoticeText));
    }

    private void RaiseDisplayLanguageRestartStateChanged()
    {
        OnPropertyChanged(nameof(IsDisplayLanguageRestartPending));
        OnPropertyChanged(nameof(HasRestartRequiredSettings));
        OnPropertyChanged(nameof(RestartRequiredTitleText));
        OnPropertyChanged(nameof(RestartRequiredNoticeText));
    }

    /// <summary>Raises bindable notifications for mainland China display settings that need a restart.</summary>
    private void RaiseMainlandChinaRestartStateChanged()
    {
        OnPropertyChanged(nameof(IsMainlandChinaDisplayRestartPending));
        OnPropertyChanged(nameof(HasRestartRequiredSettings));
        OnPropertyChanged(nameof(MainlandChinaDisplayTitleText));
        OnPropertyChanged(nameof(MainlandChinaUrlBlockingTitleText));
        OnPropertyChanged(nameof(RestartRequiredTitleText));
        OnPropertyChanged(nameof(RestartRequiredNoticeText));
    }

    /// <summary>Raises bindable notifications for live trigger engine settings.</summary>
    private void RaiseTriggerRestartStateChanged()
    {
        OnPropertyChanged(nameof(IsTriggerEngineRestartPending));
        OnPropertyChanged(nameof(TriggersEnabledTitleText));
    }

    /// <summary>Raises bindable notifications for tray icon settings that need a restart.</summary>
    private void RaiseTrayIconRestartStateChanged()
    {
        OnPropertyChanged(nameof(IsTrayIconRestartPending));
        OnPropertyChanged(nameof(HasRestartRequiredSettings));
        OnPropertyChanged(nameof(TrayUseMonochromeInactiveIconTitleText));
        OnPropertyChanged(nameof(RestartRequiredTitleText));
        OnPropertyChanged(nameof(RestartRequiredNoticeText));
    }

    /// <summary>Refreshes stable selector option collections without replacing ComboBox item sources.</summary>
    private void RefreshSelectorOptions()
    {
        List<string> languageOptions = [];
        foreach ((AppLanguage language, string displayName) in LocalizationService.GetSupportedLanguages())
        {
            languageOptions.Add(language == AppLanguage.AutoDetect
                ? _getString("Settings.Language.AutoDetect")
                : displayName);
        }

        ReplaceStableOptions(_displayLanguageOptions, languageOptions);
        ReplaceStableOptions(_appThemeModeOptions, [AppThemeFollowSystemText, AppThemeLightText, AppThemeDarkText]);
        ReplaceStableOptions(_appAccentColorModeOptions, [AppAccentColorFollowSystemText, AppAccentColorCustomText]);
        ReplaceStableOptions(_startupBehaviorModeOptions, [StartupBehaviorLastSettingText, StartupBehaviorStartRuleProxyText, StartupBehaviorDisableProxyText]);
        ReplaceStableOptions(_closeBehaviorModeOptions, [CloseBehaviorExitWithoutConfirmationText, CloseBehaviorConfirmExitText, CloseBehaviorMinimizeToTrayText]);
        ReplaceStableOptions(_mainlandChinaFeatureModeOptions, [MainlandChinaDisabledText, MainlandChinaFlagOnlyText, MainlandChinaFlagAndTextText, MainlandChinaKeywordFilterText]);
        ReplaceStableOptions(_notificationLevelOptions, [NotificationDefaultText, NotificationCriticalOnlyText, NotificationMoreText]);
    }

    private static void ReplaceStableOptions(ObservableCollection<string> target, IReadOnlyList<string> options)
    {
        for (int index = 0; index < options.Count; index++)
        {
            if (index >= target.Count)
            {
                target.Add(options[index]);
                continue;
            }

            if (!StringComparer.Ordinal.Equals(target[index], options[index]))
            {
                target[index] = options[index];
            }
        }

        while (target.Count > options.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static string NormalizeTrayVisibleFeatureIds(IEnumerable<string> ids)
    {
        HashSet<string> knownIds = TrayFeatureDefinitions
            .Select(static definition => definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> normalized = [];
        foreach (string id in ids)
        {
            string trimmedId = id.Trim();
            if (!knownIds.Contains(trimmedId) || !seen.Add(trimmedId))
            {
                continue;
            }

            normalized.Add(trimmedId);
        }

        return normalized.Count == 0 ? DefaultTrayVisibleFeatureIds : string.Join(",", normalized);
    }

    private static IEnumerable<string> SplitTrayVisibleFeatureIds(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Compares accent settings against the load-time fallback baseline.</summary>
    private bool IsAccentColorChangedSinceLoad(AppAccentColorMode mode, string colorValue)
    {
        return mode != _loadedAppAccentColorMode
            || (mode == ClashSharp.Model.AppAccentColorMode.Custom
                && _loadedAppAccentColorMode == ClashSharp.Model.AppAccentColorMode.Custom
                && !StringComparer.OrdinalIgnoreCase.Equals(colorValue, _loadedAppAccentColorValue));
    }

    /// <summary>Refreshes selector bindings after reset or language changes.</summary>
    private void RaiseSelectorBindingsChanged()
    {
        string[] propertyNames =
        [
            nameof(DisplayLanguageOptions),
            nameof(DisplayLanguageIndex),
            nameof(AppThemeModeOptions),
            nameof(AppThemeModeIndex),
            nameof(AppAccentColorModeOptions),
            nameof(AppAccentColorModeIndex),
            nameof(StartupBehaviorModeOptions),
            nameof(StartupBehaviorModeIndex),
            nameof(CloseBehaviorModeOptions),
            nameof(CloseBehaviorModeIndex),
            nameof(MainlandChinaFeatureModeOptions),
            nameof(MainlandChinaFeatureModeIndex),
        ];

        foreach (string propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    /// <summary>Raises property changes for all localized bindable text properties.</summary>
    private void RaiseLocalizedTextChanges()
    {
        RefreshSelectorOptions();
        string[] propertyNames =
        [
            nameof(PageTitleText),
            nameof(DescriptionText),
            nameof(LanguageSectionTitleText),
            nameof(LanguageTitleText),
            nameof(LanguageDescriptionText),
            nameof(DisplayLanguageOptions),
            nameof(DisplayLanguageIndex),
            nameof(AppThemeModeTitleText),
            nameof(AppThemeModeDescriptionText),
            nameof(AppThemeFollowSystemText),
            nameof(AppThemeLightText),
            nameof(AppThemeDarkText),
            nameof(AppThemeModeOptions),
            nameof(AppAccentColorTitleText),
            nameof(AppAccentColorDescriptionText),
            nameof(AppAccentColorFollowSystemText),
            nameof(AppAccentColorCustomText),
            nameof(AppAccentColorPickText),
            nameof(AppAccentColorModeOptions),
            nameof(RestartRequiredTitleText),
            nameof(RestartRequiredNoticeText),
            nameof(LaunchAtStartupTitleText),
            nameof(LaunchAtStartupDescriptionText),
            nameof(StartupSectionTitleText),
            nameof(CheckStartupConflictsTitleText),
            nameof(CheckStartupConflictsDescriptionText),
            nameof(CheckStartupConflictsNowText),
            nameof(StartupGuideTitleText),
            nameof(StartupGuideDescriptionText),
            nameof(StartupGuideShowNowText),
            nameof(StartupRestoreFallbackTitleText),
            nameof(StartupRestoreFallbackDescriptionText),
            nameof(StartupRestoreFallbackStatusText),
            nameof(RegisterText),
            nameof(DetectText),
            nameof(ProxySectionTitleText),
            nameof(TransparentProxyTitleText),
            nameof(TransparentProxyDescriptionText),
            nameof(TransparentProxyServiceTitleText),
            nameof(TransparentProxyServiceDescriptionText),
            nameof(DeployMihomoServiceText),
            nameof(UninstallMihomoServiceText),
            nameof(MihomoServiceStatusText),
            nameof(MixedPortTitleText),
            nameof(MixedPortDescriptionText),
            nameof(ConnectionTestUrlTitleText),
            nameof(ConnectionTestUrlDescriptionText),
            nameof(ConnectionTestProxyUrl1TitleText),
            nameof(ConnectionTestProxyUrl2TitleText),
            nameof(ConnectionTestDirectUrlTitleText),
            nameof(ConnectionTestStatusColumnText),
            nameof(ConnectionTestLatencyColumnText),
            nameof(ConnectionTestUrlSummaryText),
            nameof(ProxyInformationTitleText),
            nameof(ProxyInformationDescriptionText),
            nameof(ProxyLocalEntryText),
            nameof(ProxyCoreConfigurationText),
            nameof(ProxyCoreBinaryText),
            nameof(ConnectionSamplingTitleText),
            nameof(ConnectionSamplingDescriptionText),
            nameof(SamplingIntervalTitleText),
            nameof(SamplingIntervalDescriptionText),
            nameof(StartupConflictCheckTitleText),
            nameof(StartupConflictCheckDescriptionText),
            nameof(StartupBehaviorModeTitleText),
            nameof(StartupBehaviorModeDescriptionText),
            nameof(StartupBehaviorLastSettingText),
            nameof(StartupBehaviorStartRuleProxyText),
            nameof(StartupBehaviorDisableProxyText),
            nameof(StartupBehaviorModeOptions),
            nameof(TriggerSectionTitleText),
            nameof(TriggersEnabledTitleText),
            nameof(TriggersEnabledDescriptionText),
            nameof(TriggerNotificationsEnabledTitleText),
            nameof(TriggerNotificationsEnabledDescriptionText),
            nameof(IsTriggerEngineRestartPending),
            nameof(TraySectionTitleText),
            nameof(CloseBehaviorModeTitleText),
            nameof(CloseBehaviorModeDescriptionText),
            nameof(CloseBehaviorExitWithoutConfirmationText),
            nameof(CloseBehaviorConfirmExitText),
            nameof(CloseBehaviorMinimizeToTrayText),
            nameof(CloseBehaviorModeOptions),
            nameof(TrayUseMonochromeInactiveIconTitleText),
            nameof(TrayUseMonochromeInactiveIconDescriptionText),
            nameof(IsTrayIconRestartPending),
            nameof(TrayVisibleFeaturesTitleText),
            nameof(TrayVisibleFeaturesDescriptionText),
            nameof(TrayVisibleFeatureSummaryText),
            nameof(TrayVisibleFeatureSearchPlaceholderText),
            nameof(WindowsNativeSectionTitleText),
            nameof(WindowsNativeTitleText),
            nameof(WindowsNativeDescriptionText),
            nameof(OpenText),
            nameof(EditText),
            nameof(ExportText),
            nameof(ImportText),
            nameof(CheckText),
            nameof(TestText),
            nameof(WslDiagnosticTitleText),
            nameof(TerminalDiagnosticTitleText),
            nameof(StoreDiagnosticTitleText),
            nameof(DiagnoseText),
            nameof(ApplyText),
            nameof(ResetText),
            nameof(CleanupText),
            nameof(ExitApplicationText),
            nameof(RestartApplicationText),
            nameof(ApplicationLifecycleTitleText),
            nameof(ApplicationLifecycleDescriptionText),
            nameof(DiagnosticNotRunText),
            nameof(WslDiagnosticStatusText),
            nameof(TerminalDiagnosticStatusText),
            nameof(StoreDiagnosticStatusText),
            nameof(CheckStaleProxyTitleText),
            nameof(CheckStaleProxyDescriptionText),
            nameof(RestoreProxyOnExitTitleText),
            nameof(RestoreProxyOnExitDescriptionText),
            nameof(MainlandChinaSectionTitleText),
            nameof(MainlandChinaDisplayTitleText),
            nameof(MainlandChinaDisplayDescriptionText),
            nameof(MainlandChinaDisabledText),
            nameof(MainlandChinaFlagOnlyText),
            nameof(MainlandChinaFlagAndTextText),
            nameof(MainlandChinaKeywordFilterText),
            nameof(MainlandChinaAllText),
            nameof(MainlandChinaFeatureModeOptions),
            nameof(MainlandChinaUrlBlockingTitleText),
            nameof(MainlandChinaUrlBlockingDescriptionText),
            nameof(NotificationSectionTitleText),
            nameof(NotificationEnabledTitleText),
            nameof(NotificationEnabledDescriptionText),
            nameof(NotificationTitleText),
            nameof(NotificationDescriptionText),
            nameof(NotificationDefaultText),
            nameof(NotificationCriticalOnlyText),
            nameof(NotificationMoreText),
            nameof(NotificationLevelOptions),
            nameof(DataSectionTitleText),
            nameof(DataPackageTitleText),
            nameof(DataPackageDescriptionText),
            nameof(BackupRestoreTitleText),
            nameof(BackupRestoreDescriptionText),
            nameof(DataExportTitleText),
            nameof(DataExportDescriptionText),
            nameof(DataPackageScopeSettingsText),
            nameof(DataPackageScopeSettingsAndProxyConfigurationText),
            nameof(ResetAllSettingsTitleText),
            nameof(ResetAllSettingsDescriptionText),
            nameof(ClearAllDataTitleText),
            nameof(ClearAllDataDescriptionText),
            nameof(ResetGroupToDefaultsText),
            nameof(ResetGroupConfirmTitleText),
            nameof(ResetGroupConfirmMessageText),
            nameof(ResetGroupServiceDeploymentNoteText),
        ];

        foreach (string propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    /// <summary>Persists the transparent proxy switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetTransparentProxyEnabled(bool isEnabled)
    {
        if (isEnabled && !CanToggleTransparentProxy)
        {
            SetProperty(ref _transparentProxyEnabled, false, nameof(TransparentProxyEnabled));
            return;
        }

        _settings.TransparentProxyEnabled = isEnabled;
        SetProperty(ref _transparentProxyEnabled, isEnabled, nameof(TransparentProxyEnabled));
    }

    /// <summary>Deploys the mihomo Windows service and refreshes transparent proxy availability.</summary>
    /// <param name="cancellationToken">Cancels deployment wait when requested.</param>
    /// <returns>A task that completes after service status is refreshed.</returns>
    public async Task DeployMihomoServiceAsync(CancellationToken cancellationToken)
    {
        MihomoServiceStatus status = await _mihomoServiceController.DeployAsync(cancellationToken);
        SetMihomoServiceStatus(status);
        _appendLog(status.IsInstalled ? "Info" : "Warning", "MihomoService", status.Message, null);
    }

    /// <summary>Uninstalls the mihomo Windows service and preserves transparent proxy preference.</summary>
    /// <param name="cancellationToken">Cancels uninstall wait when requested.</param>
    /// <returns>A task that completes after service status is refreshed.</returns>
    public async Task UninstallMihomoServiceAsync(CancellationToken cancellationToken)
    {
        MihomoServiceStatus status = await _mihomoServiceController.UninstallAsync(cancellationToken);
        SetMihomoServiceStatus(status);
        _appendLog(status.IsInstalled ? "Warning" : "Info", "MihomoService", status.Message, null);
    }

    /// <summary>Refreshes the cached mihomo service status.</summary>
    private void RefreshMihomoServiceStatus()
    {
        SetMihomoServiceStatus(_mihomoServiceController.GetStatus());
    }

    /// <summary>Sets the cached mihomo service status and dependent bindable values.</summary>
    /// <param name="status">New service status.</param>
    private void SetMihomoServiceStatus(MihomoServiceStatus status)
    {
        _mihomoServiceStatus = status;
        MihomoServiceStatusText = status.Message;
        if (!status.IsInstalled && _settings.TransparentProxyEnabled)
        {
            _settings.TransparentProxyEnabled = false;
            SetProperty(ref _transparentProxyEnabled, false, nameof(TransparentProxyEnabled));
        }

        OnPropertyChanged(nameof(CanToggleTransparentProxy));
        DeployMihomoServiceCommand?.NotifyCanExecuteChanged();
        UninstallMihomoServiceCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Persists the launch-at-startup switch and requests system registration sync.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetLaunchAtStartupEnabled(bool isEnabled)
    {
        _settings.LaunchAtStartupEnabled = isEnabled;
        SetProperty(ref _launchAtStartupEnabled, isEnabled, nameof(LaunchAtStartupEnabled));
        _applyLaunchAtStartup(isEnabled);
    }

    /// <summary>Persists a mixed proxy port from number-box input.</summary>
    /// <param name="value">Number-box value.</param>
    /// <returns>True when the value was valid and persisted; otherwise false.</returns>
    public bool SetMixedPort(double value)
    {
        if (double.IsNaN(value))
        {
            return false;
        }

        int port = (int)Math.Round(value);
        if (port is < 1 or > 65535)
        {
            return false;
        }

        _settings.MixedPort = port;
        MixedPort = port;
        RefreshProxyInformation();
        return true;
    }

    /// <summary>Refreshes proxy information card text from the current settings and runtime paths.</summary>
    public void RefreshProxyInformation()
    {
        SettingsProxyInformation information = _getProxyInformation();
        string coreBinaryText = information.IsCoreBinaryAvailable
            ? information.CoreBinaryPath
            : _getString("Settings.ProxyInformation.CoreBinary.Missing");

        ProxyLocalEntryText = string.Format(
            _getString("Settings.ProxyInformation.LocalEntry.Format"),
            MixedPort);
        ProxyCoreConfigurationText = string.Format(
            _getString("Settings.ProxyInformation.CoreConfig.Format"),
            information.ConfigPath);
        ProxyCoreBinaryText = string.Format(
            _getString("Settings.ProxyInformation.CoreBinary.Format"),
            coreBinaryText);
    }

    /// <summary>Executes a Windows-native diagnostic command and updates the target status text.</summary>
    /// <param name="parameter">Command tag in the form "Target:Action"; null is ignored.</param>
    /// <param name="cancellationToken">Cancels the diagnostic operation when requested.</param>
    /// <returns>A task that completes after the diagnostic command is routed.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the diagnostics view model.
    /// Thread / reentrancy: UI callers should use <see cref="WindowsDiagnosticCommand"/> to prevent reentrancy.
    /// </remarks>
    public async Task ExecuteWindowsDiagnosticCommandAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (_diagnosticsViewModel is null)
        {
            return;
        }

        string? commandTag = parameter as string;
        SettingsDiagnosticStatus? status = await _diagnosticsViewModel.ExecuteCommandAsync(commandTag, cancellationToken);
        if (status is SettingsDiagnosticStatus value)
        {
            SetDiagnosticStatus(value.Target, value.Message);
        }
    }

    /// <summary>Resets all diagnostic status text to the localized not-run value.</summary>
    private void ResetDiagnosticStatusText()
    {
        WslDiagnosticStatusText = DiagnosticNotRunText;
        TerminalDiagnosticStatusText = DiagnosticNotRunText;
        StoreDiagnosticStatusText = DiagnosticNotRunText;
    }

    /// <summary>Updates the diagnostic status for one target.</summary>
    /// <param name="target">Diagnostic target.</param>
    /// <param name="message">Status message. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is unsupported.</exception>
    private void SetDiagnosticStatus(WindowsDiagnosticTarget target, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (target)
        {
            case WindowsDiagnosticTarget.Wsl:
                WslDiagnosticStatusText = message;
                break;
            case WindowsDiagnosticTarget.Terminal:
                TerminalDiagnosticStatusText = message;
                break;
            case WindowsDiagnosticTarget.MicrosoftStore:
                StoreDiagnosticStatusText = message;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported Windows diagnostic target.");
        }
    }

    /// <summary>Persists the background sampling switch and restarts sampling.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetConnectionSamplingEnabled(bool isEnabled)
    {
        _settings.ConnectionSamplingEnabled = isEnabled;
        SetProperty(ref _connectionSamplingEnabled, isEnabled, nameof(ConnectionSamplingEnabled));
        _restartConnectionSampling();
    }

    /// <summary>Persists a background sampling interval from number-box input.</summary>
    /// <param name="value">Number-box value.</param>
    /// <returns>True when the value was valid and persisted; otherwise false.</returns>
    public bool SetConnectionSamplingIntervalSeconds(double value)
    {
        if (double.IsNaN(value))
        {
            return false;
        }

        int intervalSeconds = (int)Math.Round(value);
        if (intervalSeconds is < MinConnectionSamplingIntervalSeconds or > MaxConnectionSamplingIntervalSeconds)
        {
            return false;
        }

        _settings.ConnectionSamplingIntervalSeconds = intervalSeconds;
        ConnectionSamplingIntervalSeconds = intervalSeconds;
        _restartConnectionSampling();
        return true;
    }

    /// <summary>Persists the startup conflict check switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetStartupConflictCheckEnabled(bool isEnabled)
    {
        _settings.StartupConflictCheckEnabled = isEnabled;
        SetProperty(ref _startupConflictCheckEnabled, isEnabled, nameof(StartupConflictCheckEnabled));
    }

    /// <summary>Persists a startup behavior mode selected by combo box index.</summary>
    /// <param name="index">Startup behavior enum index.</param>
    /// <returns>True when the index was valid and persisted; otherwise false.</returns>
    public bool SetStartupBehaviorModeIndex(int index)
    {
        if (!Enum.IsDefined((StartupBehaviorMode)index))
        {
            return false;
        }

        StartupBehaviorMode mode = (StartupBehaviorMode)index;
        _settings.StartupBehaviorMode = mode;
        StartupBehaviorMode = mode;
        return true;
    }

    /// <summary>Persists whether the startup guide is shown during application startup.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetShowStartupGuideOnStartup(bool isEnabled)
    {
        _settings.ShowStartupGuideOnStartup = isEnabled;
        SetProperty(ref _showStartupGuideOnStartup, isEnabled, nameof(ShowStartupGuideOnStartup));
    }

    /// <summary>Persists whether trigger evaluation is enabled.</summary>
    public void SetTriggersEnabled(bool isEnabled)
    {
        _settings.TriggersEnabled = isEnabled;
        if (SetProperty(ref _triggersEnabled, isEnabled, nameof(TriggersEnabled)))
        {
            RaiseTriggerRestartStateChanged();
        }
    }

    /// <summary>Persists whether fired triggers send dedicated notifications.</summary>
    public void SetTriggerNotificationsEnabled(bool isEnabled)
    {
        _settings.TriggerNotificationsEnabled = isEnabled;
        SetProperty(ref _triggerNotificationsEnabled, isEnabled, nameof(TriggerNotificationsEnabled));
    }

    /// <summary>Persists the close behavior selected by combo box index.</summary>
    public bool SetCloseBehaviorModeIndex(int index)
    {
        if (!Enum.IsDefined((CloseBehaviorMode)index))
        {
            return false;
        }

        CloseBehaviorMode mode = (CloseBehaviorMode)index;
        _settings.CloseBehaviorMode = mode;
        CloseBehaviorMode = mode;
        return true;
    }

    /// <summary>Persists whether the inactive tray icon uses a monochrome logo.</summary>
    public void SetTrayUseMonochromeInactiveIcon(bool isEnabled)
    {
        _settings.TrayUseMonochromeInactiveIcon = isEnabled;
        if (SetProperty(ref _trayUseMonochromeInactiveIcon, isEnabled, nameof(TrayUseMonochromeInactiveIcon)))
        {
            RaiseTrayIconRestartStateChanged();
        }
    }

    /// <summary>Persists selected tray feature ids.</summary>
    public void SetTrayVisibleFeatureIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _settings.TrayVisibleFeatureIds = NormalizeTrayVisibleFeatureIds(ids);
        TrayVisibleFeatureIds = _settings.TrayVisibleFeatureIds;
    }

    /// <summary>Gets visible tray feature definitions in persisted order.</summary>
    public IReadOnlyList<SettingsTrayFeatureDefinition> GetTrayVisibleFeatureDefinitions()
    {
        Dictionary<string, SettingsTrayFeatureDefinition> definitions = TrayFeatureDefinitions.ToDictionary(
            static definition => definition.Id,
            StringComparer.OrdinalIgnoreCase);
        List<SettingsTrayFeatureDefinition> selected = [];
        foreach (string id in SplitTrayVisibleFeatureIds(TrayVisibleFeatureIds))
        {
            if (definitions.TryGetValue(id, out SettingsTrayFeatureDefinition definition))
            {
                selected.Add(definition);
            }
        }

        return selected.Count == 0 ? TrayFeatureDefinitions : selected;
    }

    /// <summary>Refreshes the startup restore fallback registration status text.</summary>
    public void RefreshStartupRestoreFallbackStatus()
    {
        StartupRestoreFallbackStatus status = StartupRestoreFallbackService.Instance.GetStatus();
        StartupRestoreFallbackStatusText = _getString(status.IsRegistered
            ? "Settings.StartupRestoreFallback.Status.Registered"
            : "Settings.StartupRestoreFallback.Status.NotRegistered");
    }

    /// <summary>Registers the startup restore fallback helper and refreshes status.</summary>
    public void RegisterStartupRestoreFallback()
    {
        StartupRestoreFallbackService.Instance.Register();
        RefreshStartupRestoreFallbackStatus();
    }

    /// <summary>Uninstalls the startup restore fallback helper and refreshes status.</summary>
    public void UninstallStartupRestoreFallback()
    {
        StartupRestoreFallbackService.Instance.Uninstall();
        RefreshStartupRestoreFallbackStatus();
    }

    /// <summary>Persists the stale proxy startup check switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetCheckStaleProxyOnStartup(bool isEnabled)
    {
        _settings.CheckStaleProxyOnStartup = isEnabled;
        SetProperty(ref _checkStaleProxyOnStartup, isEnabled, nameof(CheckStaleProxyOnStartup));
    }

    /// <summary>Persists the shutdown proxy restoration switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetRestoreProxyOnExit(bool isEnabled)
    {
        _settings.RestoreProxyOnExit = isEnabled;
        SetProperty(ref _restoreProxyOnExit, isEnabled, nameof(RestoreProxyOnExit));
    }

    /// <summary>Persists a mainland China feature mode selected by combo box index.</summary>
    /// <param name="index">Feature mode enum index.</param>
    /// <returns>True when the index was valid and persisted; otherwise false.</returns>
    public bool SetMainlandChinaFeatureModeIndex(int index)
    {
        if (!Enum.IsDefined((MainlandChinaFeatureMode)index))
        {
            return false;
        }

        MainlandChinaFeatureMode mode = (MainlandChinaFeatureMode)index;
        if (mode == MainlandChinaFeatureMode.AllIncludingUrlBlacklist)
        {
            mode = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;
        }

        _settings.MainlandChinaFeatureMode = mode;
        MainlandChinaFeatureMode = mode;
        return true;
    }

    /// <summary>Persists the mainland China URL blocking switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetMainlandChinaUrlBlockingEnabled(bool isEnabled)
    {
        _settings.MainlandChinaUrlBlockingEnabled = isEnabled;
        if (SetProperty(ref _mainlandChinaUrlBlockingEnabled, isEnabled, nameof(MainlandChinaUrlBlockingEnabled)))
        {
            RaiseMainlandChinaRestartStateChanged();
        }
    }

    /// <summary>Persists a notification verbosity selected by combo box index.</summary>
    /// <param name="index">Notification level enum index.</param>
    /// <returns>True when the index was valid and persisted; otherwise false.</returns>
    public bool SetNotificationLevelIndex(int index)
    {
        if (!Enum.IsDefined((NotificationLevel)index))
        {
            return false;
        }

        NotificationLevel level = (NotificationLevel)index;
        _settings.NotificationLevel = level;
        NotificationLevel = level;
        return true;
    }

    /// <summary>Persists whether Windows system notifications are enabled.</summary>
    /// <param name="isEnabled">True to show notifications subject to level filtering.</param>
    public void SetNotificationEnabled(bool isEnabled)
    {
        _settings.NotificationEnabled = isEnabled;
        SetProperty(ref _notificationEnabled, isEnabled, nameof(NotificationEnabled));
    }

    /// <summary>Persists the proxy connection-test URL.</summary>
    /// <param name="value">User-entered URL.</param>
    /// <returns>True when the value was valid and persisted; otherwise false.</returns>
    public bool SetConnectionTestUrl(string value)
    {
        if (!TryNormalizeConnectionTestUrl(value, out string persistedValue))
        {
            return false;
        }

        _settings.ConnectionTestUrl = persistedValue;
        ConnectionTestUrl = persistedValue;
        return true;
    }

    /// <summary>Persists all registered connection-test URLs.</summary>
    public bool SetConnectionTestUrls(string proxyUrl1, string proxyUrl2, string directUrl)
    {
        if (!TryNormalizeConnectionTestUrl(proxyUrl1, out string normalizedProxyUrl1)
            || !TryNormalizeConnectionTestUrl(proxyUrl2, out string normalizedProxyUrl2)
            || !TryNormalizeConnectionTestUrl(directUrl, out string normalizedDirectUrl))
        {
            return false;
        }

        _settings.ConnectionTestProxyUrl1 = normalizedProxyUrl1;
        _settings.ConnectionTestProxyUrl2 = normalizedProxyUrl2;
        _settings.ConnectionTestDirectUrl = normalizedDirectUrl;
        ConnectionTestProxyUrl1 = normalizedProxyUrl1;
        ConnectionTestProxyUrl2 = normalizedProxyUrl2;
        ConnectionTestDirectUrl = normalizedDirectUrl;
        return true;
    }

    /// <summary>Restores registered connection-test URLs to defaults.</summary>
    public void ResetConnectionTestUrlsToDefaults()
    {
        _settings.ConnectionTestProxyUrl1 = DefaultConnectionTestProxyUrl1;
        _settings.ConnectionTestProxyUrl2 = DefaultConnectionTestProxyUrl2;
        _settings.ConnectionTestDirectUrl = DefaultConnectionTestDirectUrl;
        ConnectionTestProxyUrl1 = _settings.ConnectionTestProxyUrl1;
        ConnectionTestProxyUrl2 = _settings.ConnectionTestProxyUrl2;
        ConnectionTestDirectUrl = _settings.ConnectionTestDirectUrl;
    }

    private string FormatConnectionTestUrlSummaryPart(string url)
    {
        string host = ExtractNormalizedHost(url);
        foreach ((string resourceKey, string[] hosts) in KnownConnectionTestUrlHosts)
        {
            foreach (string knownHost in hosts)
            {
                if (host.Equals(knownHost, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith($".{knownHost}", StringComparison.OrdinalIgnoreCase))
                {
                    return _getString(resourceKey);
                }
            }
        }

        return _getString("Settings.ConnectionTestUrl.Provider.Custom");
    }

    private static string ExtractNormalizedHost(string value)
    {
        if (!TryNormalizeConnectionTestUrl(value, out string normalizedUrl)
            || !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
    }

    private static bool TryNormalizeConnectionTestUrl(string value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalizedValue = value.Trim();
        if (!normalizedValue.Contains("://", StringComparison.Ordinal))
        {
            normalizedValue = $"https://{normalizedValue}";
        }

        if (!Uri.TryCreate(normalizedValue, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalizedUrl = uri.ToString().TrimEnd('/');
        return true;
    }

    /// <summary>Runs a connection test against the persisted connection-test URLs.</summary>
    /// <param name="cancellationToken">Cancels the test when requested.</param>
    /// <returns>Structured target rows and localized summary text.</returns>
    public async Task<ConnectionTestReport> RunConnectionTestAsync(CancellationToken cancellationToken)
    {
        List<ConnectionTestTargetResult> results = [];
        try
        {
            IsConnectionTestRunning = true;
            (string Label, string Url)[] targets =
            [
                (ConnectionTestProxyUrl1TitleText, ConnectionTestProxyUrl1),
                (ConnectionTestProxyUrl2TitleText, ConnectionTestProxyUrl2),
                (ConnectionTestDirectUrlTitleText, ConnectionTestDirectUrl),
            ];

            results.AddRange(await Task.WhenAll(targets.Select(target => RunConnectionTestTargetAsync(target.Label, target.Url, cancellationToken))));

            ConnectionTestSummaryState summaryState = BuildConnectionTestSummaryState(results);
            ConnectionTestReport report = new(results, BuildConnectionTestSummary(results, summaryState), summaryState);
            AppendConnectionTestLog(report);
            return report;
        }
        finally
        {
            IsConnectionTestRunning = false;
        }
    }

    private async Task<ConnectionTestTargetResult> RunConnectionTestTargetAsync(string label, string url, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            Uri uri = new(url);
            int statusCode = await _testConnectionAsync(uri, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            bool succeeded = statusCode is >= 200 and < 400;
            return new ConnectionTestTargetResult(
                label,
                url,
                succeeded,
                string.Format(_getString("Settings.ConnectionTest.StatusHttp.Format"), statusCode),
                FormatLatency(stopwatch.Elapsed),
                (int)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            _notifyConnectionTestTimeout(url);
            return new ConnectionTestTargetResult(
                label,
                url,
                false,
                _getString("Settings.ConnectionTest.TimedOut"),
                FormatLatency(stopwatch.Elapsed),
                null);
        }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException)
        {
            stopwatch.Stop();
            return new ConnectionTestTargetResult(
                label,
                url,
                false,
                string.Format(_getString("Settings.ConnectionTest.Failed.Format"), exception.Message),
                FormatLatency(stopwatch.Elapsed),
                null);
        }
    }

    private void AppendConnectionTestLog(ConnectionTestReport report)
    {
        string level = report.SummaryState == ConnectionTestSummaryState.AllPassed ? "Info" : "Warning";
        string detail = string.Join(
            Environment.NewLine,
            report.Results.Select(result => $"{result.Label} | {result.Url} | {result.StatusText} | {result.LatencyText}"));
        _appendLog(level, "ConnectionTest", report.SummaryText, detail);
    }

    private static ConnectionTestSummaryState BuildConnectionTestSummaryState(IReadOnlyList<ConnectionTestTargetResult> results)
    {
        if (results.All(static result => result.Succeeded))
        {
            return ConnectionTestSummaryState.AllPassed;
        }

        if (results.All(static result => !result.Succeeded))
        {
            return ConnectionTestSummaryState.AllFailed;
        }

        return ConnectionTestSummaryState.PartialFailed;
    }

    private string BuildConnectionTestSummary(IReadOnlyList<ConnectionTestTargetResult> results, ConnectionTestSummaryState summaryState)
    {
        if (summaryState is ConnectionTestSummaryState.AllPassed)
        {
            return _getString("Settings.ConnectionTest.AllPassed");
        }

        if (summaryState is ConnectionTestSummaryState.AllFailed)
        {
            return _getString("Settings.ConnectionTest.AllFailed");
        }

        int passed = results.Count(static result => result.Succeeded);
        return string.Format(_getString("Settings.ConnectionTest.PartialPassed.Format"), passed, results.Count);
    }

    private static string FormatLatency(TimeSpan elapsed)
    {
        int milliseconds = Math.Max(0, (int)Math.Round(elapsed.TotalMilliseconds));
        return $"{milliseconds} ms";
    }

    /// <summary>Restores base display settings to defaults.</summary>
    public void ResetBasicSettingsToDefaults()
    {
        _settings.DisplayLanguage = AppLanguage.AutoDetect;
        DisplayLanguage = AppLanguage.AutoDetect;
        RaiseDisplayLanguageRestartStateChanged();

        _settings.AppThemeMode = AppThemeMode.FollowSystem;
        AppThemeMode = AppThemeMode.FollowSystem;
        _applyTheme(AppThemeMode.FollowSystem);

        _settings.AppAccentColorMode = AppAccentColorMode.FollowSystem;
        _settings.AppAccentColorValue = DefaultAppAccentColorValue;
        AppAccentColorMode = AppAccentColorMode.FollowSystem;
        AppAccentColorValue = _settings.AppAccentColorValue;

        _settings.CloseBehaviorMode = CloseBehaviorMode.MinimizeToTray;
        CloseBehaviorMode = CloseBehaviorMode.MinimizeToTray;

        RaiseSelectorBindingsChanged();
        RefreshProxyInformation();
        ResetDiagnosticStatusText();
    }

    /// <summary>Restores notification settings to defaults.</summary>
    public void ResetNotificationSettingsToDefaults()
    {
        _settings.NotificationEnabled = true;
        SetProperty(ref _notificationEnabled, true, nameof(NotificationEnabled));

        _settings.NotificationLevel = NotificationLevel.Default;
        NotificationLevel = NotificationLevel.Default;
        RaiseSelectorBindingsChanged();
    }

    /// <summary>Restores startup settings to defaults.</summary>
    public void ResetStartupSettingsToDefaults()
    {
        _settings.LaunchAtStartupEnabled = false;
        SetProperty(ref _launchAtStartupEnabled, false, nameof(LaunchAtStartupEnabled));
        _applyLaunchAtStartup(false);

        _settings.StartupConflictCheckEnabled = true;
        SetProperty(ref _startupConflictCheckEnabled, true, nameof(StartupConflictCheckEnabled));

        _settings.ShowStartupGuideOnStartup = true;
        SetProperty(ref _showStartupGuideOnStartup, true, nameof(ShowStartupGuideOnStartup));

        _settings.StartupBehaviorMode = StartupBehaviorMode.LastSetting;
        StartupBehaviorMode = StartupBehaviorMode.LastSetting;
        RaiseSelectorBindingsChanged();
    }

    /// <summary>Restores trigger settings to defaults.</summary>
    public void ResetTriggerSettingsToDefaults()
    {
        _settings.TriggersEnabled = true;
        if (SetProperty(ref _triggersEnabled, true, nameof(TriggersEnabled)))
        {
            RaiseTriggerRestartStateChanged();
        }

        _settings.TriggerNotificationsEnabled = true;
        SetProperty(ref _triggerNotificationsEnabled, true, nameof(TriggerNotificationsEnabled));
    }

    /// <summary>Restores taskbar tray settings to defaults without changing deployed services.</summary>
    public void ResetTraySettingsToDefaults()
    {
        _settings.TrayUseMonochromeInactiveIcon = false;
        if (SetProperty(ref _trayUseMonochromeInactiveIcon, false, nameof(TrayUseMonochromeInactiveIcon)))
        {
            RaiseTrayIconRestartStateChanged();
        }

        _settings.TrayVisibleFeatureIds = DefaultTrayVisibleFeatureIds;
        TrayVisibleFeatureIds = _settings.TrayVisibleFeatureIds;

        RaiseSelectorBindingsChanged();
    }

    /// <summary>Restores transparent proxy settings to defaults.</summary>
    public void ResetTransparentProxySettingsToDefaults()
    {
        SetTransparentProxyEnabled(CanToggleTransparentProxy);
    }

    /// <summary>Restores proxy runtime settings to defaults.</summary>
    public void ResetProxySettingsToDefaults()
    {
        SetTransparentProxyEnabled(CanToggleTransparentProxy);

        _settings.MixedPort = DefaultMixedPort;
        MixedPort = DefaultMixedPort;

        _settings.ConnectionSamplingEnabled = true;
        SetProperty(ref _connectionSamplingEnabled, true, nameof(ConnectionSamplingEnabled));

        _settings.ConnectionSamplingIntervalSeconds = DefaultConnectionSamplingIntervalSeconds;
        ConnectionSamplingIntervalSeconds = DefaultConnectionSamplingIntervalSeconds;

        _settings.ConnectionTestUrl = DefaultConnectionTestUrl;
        ConnectionTestUrl = _settings.ConnectionTestUrl;
        ResetConnectionTestUrlsToDefaults();

        _restartConnectionSampling();
        RefreshProxyInformation();
    }

    /// <summary>Restores Windows-native repair settings to defaults.</summary>
    public void ResetWindowsNativeSettingsToDefaults()
    {
        _settings.CheckStaleProxyOnStartup = true;
        SetProperty(ref _checkStaleProxyOnStartup, true, nameof(CheckStaleProxyOnStartup));

        _settings.RestoreProxyOnExit = true;
        SetProperty(ref _restoreProxyOnExit, true, nameof(RestoreProxyOnExit));
    }

    /// <summary>Restores mainland China feature settings to defaults.</summary>
    public void ResetMainlandChinaSettingsToDefaults()
    {
        _settings.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementAndTextCompletion;
        MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementAndTextCompletion;

        _settings.MainlandChinaUrlBlockingEnabled = false;
        SetProperty(ref _mainlandChinaUrlBlockingEnabled, false, nameof(MainlandChinaUrlBlockingEnabled));
        RaiseMainlandChinaRestartStateChanged();
        RaiseSelectorBindingsChanged();
    }

    /// <summary>Resets all persisted settings through the injected maintenance action and reloads the view model.</summary>
    public void ResetAllSettings()
    {
        _resetAllSettings();
        ReloadAfterMaintenance();
    }

    /// <summary>Clears all local application data through the injected maintenance action and reloads the view model.</summary>
    public void ClearAllData()
    {
        _clearAllData();
        ReloadAfterMaintenance();
    }

    public void ExitApplication()
    {
        _exitApplication();
    }

    public void RestartApplication()
    {
        _restartApplication();
    }

    /// <summary>Checks startup conflicts for the currently configured mixed port.</summary>
    /// <returns>Detected startup conflict issues.</returns>
    public IReadOnlyList<StartupConflictIssue> CheckStartupConflicts()
    {
        return _checkStartupConflicts(MixedPort);
    }

    private void ReloadAfterMaintenance()
    {
        Load();
        RaiseLocalizedTextChanges();
        RaiseSelectorBindingsChanged();
        ResetDiagnosticStatusText();
    }

    private static async Task<int> TestConnectionAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(ConnectionTestTimeoutSeconds),
        };
        using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken);
        return (int)response.StatusCode;
    }
}
