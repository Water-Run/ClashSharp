using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Owns user-editable settings state and persistence for the settings page.</summary>
/// <remarks>
/// Invariants: Numeric values exposed by properties remain within the persisted settings range.
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

    /// <summary>Unexpected error sink used by handled operations that still require diagnostics.</summary>
    private readonly IApplicationErrorSink _errorSink;

    /// <summary>Callback invoked when the display language changes.</summary>
    private readonly Action<AppLanguage> _applyLanguage;

    /// <summary>Callback invoked when the display style changes.</summary>
    private readonly Action<AppThemeMode> _applyTheme;

    /// <summary>Callback invoked when the application accent color changes.</summary>
    private readonly Action<AppAccentColorMode, string> _applyAccentColor;

    /// <summary>Callback invoked when launch-at-startup changes.</summary>
    private readonly Func<bool, CancellationToken, Task> _applyLaunchAtStartupAsync;

    /// <summary>Callback invoked when background connection sampling settings change.</summary>
    private readonly Func<CancellationToken, Task> _restartConnectionSamplingAsync;

    /// <summary>Applies requested TUN and mixed-port values through the verified runtime transaction.</summary>
    private readonly Func<bool, int, CancellationToken, Task> _applyNetworkSettingsAsync;

    /// <summary>Drains process-wide mutations for full settings commit and compensation.</summary>
    private readonly Func<CancellationToken, ValueTask<ISettingsDestructiveRuntimeScope>>
        _beginDestructiveRuntimeMutationAsync;

    private bool _appliedLaunchAtStartup;

    private bool _pendingLaunchAtStartup;

    private bool _appliedConnectionSamplingEnabled;

    private int _appliedConnectionSamplingIntervalSeconds;

    private int _connectionSamplingRevision;

    private bool _appliedTransparentProxyEnabled;

    private int _appliedMixedPort;

    private int _networkSettingsRevision;

    private int _appliedNetworkSettingsRevision;

    private readonly SemaphoreSlim _networkSettingsGate = new(1, 1);

    /// <summary>Serializes full reset commit points within this view model.</summary>
    private readonly SemaphoreSlim _resetSettingsGate = new(1, 1);

    private Task _networkSettingsRequeueTask = Task.CompletedTask;

    /// <summary>Localization resolver used by bindable settings labels.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Immutable supported-language catalog supplied by the composition boundary.</summary>
    private readonly IReadOnlyList<(AppLanguage Language, string DisplayName)> _supportedLanguages;

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

    /// <summary>Starts a reset generation while retaining the complete previous settings snapshot.</summary>
    private readonly Func<ISettingsResetTransactionReceipt> _beginResetSettings;

    /// <summary>Callback that clears all local application data.</summary>
    private readonly Func<CancellationToken, Task> _clearAllDataAsync;

    private readonly Action _exitApplication;

    private readonly Action _restartApplication;

    /// <summary>Requests the mandatory process restart used after reset compensation cannot converge.</summary>
    private readonly Func<bool> _requestResetRecoveryRestart;

    private readonly Func<bool> _isStartupRestoreFallbackRegistered;

    private readonly Action _registerStartupRestoreFallback;

    private readonly Action _uninstallStartupRestoreFallback;

    /// <summary>Startup conflict checker.</summary>
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<StartupConflictIssue>>> _checkStartupConflictsAsync;

    /// <summary>Compares desired accent color settings against the currently applied app accent state.</summary>
    private readonly Func<AppAccentColorMode, string, bool> _isAccentColorRestartPending;

    /// <summary>Diagnostics command router used by Windows-native diagnostic buttons.</summary>
    private readonly SettingsDiagnosticsViewModel? _diagnosticsViewModel;

    /// <summary>Mihomo service controller used by transparent proxy settings.</summary>
    private readonly IMihomoServiceController _mihomoServiceController;

#if UNIT_TESTS
    private static IReadOnlyList<(AppLanguage Language, string DisplayName)> TestSupportedLanguages { get; } =
        Array.AsReadOnly<(AppLanguage Language, string DisplayName)>(
        [
            (AppLanguage.AutoDetect, "Auto"),
            (AppLanguage.SimplifiedChinese, "简体中文"),
            (AppLanguage.TraditionalChinese, "繁體中文"),
            (AppLanguage.English, "English"),
            (AppLanguage.Russian, "Русский"),
            (AppLanguage.French, "Français"),
            (AppLanguage.German, "Deutsch"),
        ]);

    private static void NoOpLifecycleAction()
    {
    }

    private static Task<int> SuccessfulConnectionTestAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(204);
    }

    /// <summary>Initializes a test-facing settings view model with the smallest dependency set.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling)
        : this(
            settings,
            applyLanguage,
            _ => { },
            restartConnectionSampling,
            _ => { },
            key => key,
            () => new SettingsProxyInformation(string.Empty, false, string.Empty),
            TestOnlyApplicationErrorSink.Shared,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            static () => false,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            SuccessfulConnectionTestAsync)
    {
    }

    /// <summary>Initializes a test-facing settings view model with language and theme callbacks.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action<AppThemeMode> applyTheme,
        Action restartConnectionSampling)
        : this(
            settings,
            applyLanguage,
            applyTheme,
            restartConnectionSampling,
            _ => { },
            key => key,
            () => new SettingsProxyInformation(string.Empty, false, string.Empty),
            TestOnlyApplicationErrorSink.Shared,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            static () => false,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            SuccessfulConnectionTestAsync)
    {
    }

    /// <summary>Initializes a test-facing settings view model with an explicit mihomo service controller.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling,
        IMihomoServiceController mihomoServiceController)
        : this(
            settings,
            applyLanguage,
            _ => { },
            restartConnectionSampling,
            _ => { },
            key => key,
            () => new SettingsProxyInformation(string.Empty, false, string.Empty),
            TestOnlyApplicationErrorSink.Shared,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            static () => false,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            SuccessfulConnectionTestAsync,
            mihomoServiceController: mihomoServiceController)
    {
    }

    /// <summary>Initializes a test-facing settings view model with a localization resolver.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling,
        Func<string, string> getString)
        : this(
            settings,
            applyLanguage,
            _ => { },
            restartConnectionSampling,
            _ => { },
            getString,
            () => new SettingsProxyInformation(string.Empty, false, string.Empty),
            TestOnlyApplicationErrorSink.Shared,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            static () => false,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            SuccessfulConnectionTestAsync)
    {
    }

    /// <summary>Initializes a test-facing settings view model with launch-at-startup behavior.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action<AppThemeMode> applyTheme,
        Action restartConnectionSampling,
        Action<bool> applyLaunchAtStartup)
        : this(
            settings,
            applyLanguage,
            applyTheme,
            restartConnectionSampling,
            applyLaunchAtStartup,
            key => key,
            () => new SettingsProxyInformation(string.Empty, false, string.Empty),
            TestOnlyApplicationErrorSink.Shared,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            static () => false,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            SuccessfulConnectionTestAsync)
    {
    }

    /// <summary>Initializes a test-facing settings view model with proxy information.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling,
        Func<string, string> getString,
        Func<SettingsProxyInformation> getProxyInformation,
        SettingsDiagnosticsViewModel? diagnosticsViewModel = null)
        : this(
            settings,
            applyLanguage,
            _ => { },
            restartConnectionSampling,
            _ => { },
            getString,
            getProxyInformation,
            TestOnlyApplicationErrorSink.Shared,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            static () => false,
            NoOpLifecycleAction,
            NoOpLifecycleAction,
            SuccessfulConnectionTestAsync,
            diagnosticsViewModel)
    {
    }
#endif

    /// <summary>Initializes a new settings view model with localization, theme, startup, and proxy information providers.</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action<AppThemeMode> applyTheme,
        Action restartConnectionSampling,
        Action<bool> applyLaunchAtStartup,
        Func<string, string> getString,
        Func<SettingsProxyInformation> getProxyInformation,
        IApplicationErrorSink errorSink,
        Action exitApplication,
        Action restartApplication,
        Func<bool> isStartupRestoreFallbackRegistered,
        Action registerStartupRestoreFallback,
        Action uninstallStartupRestoreFallback,
        Func<Uri, CancellationToken, Task<int>> testConnectionAsync,
        SettingsDiagnosticsViewModel? diagnosticsViewModel = null,
        IMihomoServiceController? mihomoServiceController = null,
        Action<AppAccentColorMode, string>? applyAccentColor = null,
        Action? resetAllSettings = null,
        Action? clearAllData = null,
        Func<int, CancellationToken, Task<IReadOnlyList<StartupConflictIssue>>>? checkStartupConflictsAsync = null,
        Func<AppAccentColorMode, string, bool>? isAccentColorRestartPending = null,
        Action<string>? notifyConnectionTestTimeout = null,
        Action<string, string, string, string?>? appendLog = null,
        Func<CancellationToken, Task>? restartConnectionSamplingAsync = null,
        Func<bool, CancellationToken, Task>? applyLaunchAtStartupAsync = null,
        Func<CancellationToken, Task>? clearAllDataAsync = null,
        IReadOnlyList<(AppLanguage Language, string DisplayName)>? supportedLanguages = null,
        Func<bool, int, CancellationToken, Task>? applyNetworkSettingsAsync = null,
        Func<bool>? requestResetRecoveryRestart = null,
        Func<CancellationToken, ValueTask<ISettingsDestructiveRuntimeScope>>?
            beginDestructiveRuntimeMutationAsync = null,
        Func<ISettingsResetTransactionReceipt>? beginResetSettings = null)
        : this(
            settings,
            applyLanguage,
            applyTheme,
            restartConnectionSampling,
            applyLaunchAtStartup,
            getString,
            getProxyInformation,
            errorSink,
            diagnosticsViewModel,
            mihomoServiceController,
            applyAccentColor,
            testConnectionAsync,
            resetAllSettings,
            clearAllData,
            checkStartupConflictsAsync,
            isAccentColorRestartPending,
            notifyConnectionTestTimeout,
            appendLog,
            restartConnectionSamplingAsync,
            applyLaunchAtStartupAsync,
            clearAllDataAsync,
            exitApplication,
            restartApplication,
            isStartupRestoreFallbackRegistered,
            registerStartupRestoreFallback,
            uninstallStartupRestoreFallback,
            supportedLanguages,
            applyNetworkSettingsAsync,
            requestResetRecoveryRestart,
            beginDestructiveRuntimeMutationAsync,
            beginResetSettings)
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
        IApplicationErrorSink errorSink,
        SettingsDiagnosticsViewModel? diagnosticsViewModel,
        IMihomoServiceController? mihomoServiceController,
        Action<AppAccentColorMode, string>? applyAccentColor,
        Func<Uri, CancellationToken, Task<int>>? testConnectionAsync,
        Action? resetAllSettings,
        Action? clearAllData,
        Func<int, CancellationToken, Task<IReadOnlyList<StartupConflictIssue>>>? checkStartupConflictsAsync,
        Func<AppAccentColorMode, string, bool>? isAccentColorRestartPending,
        Action<string>? notifyConnectionTestTimeout,
        Action<string, string, string, string?>? appendLog,
        Func<CancellationToken, Task>? restartConnectionSamplingAsync,
        Func<bool, CancellationToken, Task>? applyLaunchAtStartupAsync,
        Func<CancellationToken, Task>? clearAllDataAsync,
        Action? exitApplication,
        Action? restartApplication,
        Func<bool>? isStartupRestoreFallbackRegistered,
        Action? registerStartupRestoreFallback,
        Action? uninstallStartupRestoreFallback,
        IReadOnlyList<(AppLanguage Language, string DisplayName)>? supportedLanguages,
        Func<bool, int, CancellationToken, Task>? applyNetworkSettingsAsync,
        Func<bool>? requestResetRecoveryRestart,
        Func<CancellationToken, ValueTask<ISettingsDestructiveRuntimeScope>>?
            beginDestructiveRuntimeMutationAsync = null,
        Func<ISettingsResetTransactionReceipt>? beginResetSettings = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        _applyLanguage = applyLanguage ?? throw new ArgumentNullException(nameof(applyLanguage));
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        _applyAccentColor = applyAccentColor ?? ((_, _) => { });
        ArgumentNullException.ThrowIfNull(applyLaunchAtStartup);
        ArgumentNullException.ThrowIfNull(restartConnectionSampling);
        _applyLaunchAtStartupAsync = applyLaunchAtStartupAsync ?? ((isEnabled, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            applyLaunchAtStartup(isEnabled);
            return Task.CompletedTask;
        });
        _restartConnectionSamplingAsync = restartConnectionSamplingAsync ?? (cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            restartConnectionSampling();
            return Task.CompletedTask;
        });
        _applyNetworkSettingsAsync = applyNetworkSettingsAsync
            ?? ((transparentProxyEnabled, mixedPort, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                settings.TransparentProxyEnabled = transparentProxyEnabled;
                settings.MixedPort = mixedPort;
                return Task.CompletedTask;
            });
        _resetAllSettings = resetAllSettings ?? (() => { });
        _beginResetSettings = beginResetSettings
            ?? new Func<ISettingsResetTransactionReceipt>(
                () => new LegacySettingsResetTransactionReceipt(_resetAllSettings));
        _beginDestructiveRuntimeMutationAsync = beginDestructiveRuntimeMutationAsync
            ?? (cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<ISettingsDestructiveRuntimeScope>(
                    new PassthroughDestructiveRuntimeScope(
                        _applyLaunchAtStartupAsync,
                        _restartConnectionSamplingAsync,
                        _applyNetworkSettingsAsync,
                        () => _beginResetSettings(),
                        snapshot =>
                        {
                            settings.DisplayLanguage = snapshot.DisplayLanguage;
                            settings.AppThemeMode = snapshot.AppThemeMode;
                            settings.AppAccentColorMode = snapshot.AppAccentColorMode;
                            settings.AppAccentColorValue = snapshot.AppAccentColorValue;
                            settings.LaunchAtStartupEnabled = snapshot.LaunchAtStartupEnabled;
                            settings.ConnectionSamplingEnabled = snapshot.ConnectionSamplingEnabled;
                            settings.ConnectionSamplingIntervalSeconds = snapshot.ConnectionSamplingIntervalSeconds;
                            settings.CurrentMode = snapshot.CurrentMode;
                            settings.ActiveProfileId = snapshot.ActiveProfileId;
                            settings.TransparentProxyEnabled = snapshot.TransparentProxyEnabled;
                            settings.MixedPort = snapshot.MixedPort;
                        }));
            });
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
#if UNIT_TESTS
        supportedLanguages ??= TestSupportedLanguages;
#endif
        ArgumentNullException.ThrowIfNull(supportedLanguages);
        _supportedLanguages = Array.AsReadOnly(supportedLanguages.ToArray());
        _getProxyInformation = getProxyInformation ?? throw new ArgumentNullException(nameof(getProxyInformation));
        _testConnectionAsync = testConnectionAsync ?? throw new ArgumentNullException(nameof(testConnectionAsync));
        _notifyConnectionTestTimeout = notifyConnectionTestTimeout ?? (_ => { });
        _appendLog = appendLog ?? ((_, _, _, _) => { });
        _clearAllDataAsync = clearAllDataAsync ?? (cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            clearAllData?.Invoke();
            return Task.CompletedTask;
        });
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
        _restartApplication = restartApplication ?? throw new ArgumentNullException(nameof(restartApplication));
        _requestResetRecoveryRestart = requestResetRecoveryRestart ?? (() =>
        {
            _restartApplication();
            return true;
        });
        _isStartupRestoreFallbackRegistered = isStartupRestoreFallbackRegistered
            ?? throw new ArgumentNullException(nameof(isStartupRestoreFallbackRegistered));
        _registerStartupRestoreFallback = registerStartupRestoreFallback
            ?? throw new ArgumentNullException(nameof(registerStartupRestoreFallback));
        _uninstallStartupRestoreFallback = uninstallStartupRestoreFallback
            ?? throw new ArgumentNullException(nameof(uninstallStartupRestoreFallback));
        _checkStartupConflictsAsync = checkStartupConflictsAsync
            ?? ((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<StartupConflictIssue>>([]);
            });
        _isAccentColorRestartPending = isAccentColorRestartPending ?? IsAccentColorChangedSinceLoad;
        _diagnosticsViewModel = diagnosticsViewModel;
        _mihomoServiceController = mihomoServiceController ?? AlwaysAvailableMihomoServiceController.Instance;
        RefreshSelectorOptions();
        WindowsDiagnosticCommand = new AsyncRelayCommand(
            ExecuteWindowsDiagnosticCommandAsync,
            errorSink,
            operationName: "settings-windows-diagnostic");
        RefreshMihomoServiceStatusCommand = new AsyncRelayCommand(
            RefreshMihomoServiceStatusAsync,
            errorSink,
            operationName: "settings-refresh-mihomo-service");
        ApplyLaunchAtStartupCommand = new AsyncRelayCommand(
            SynchronizeLaunchAtStartupAsync,
            errorSink,
            operationName: "settings-launch-at-startup");
        RestartConnectionSamplingCommand = new AsyncRelayCommand(
            SynchronizeConnectionSamplingAsync,
            errorSink,
            operationName: "settings-connection-sampling");
        ApplyNetworkSettingsCommand = new AsyncRelayCommand(
            SynchronizeNetworkSettingsAsync,
            errorSink,
            operationName: "settings-network-runtime");
        ExitApplicationCommand = new RelayCommand(ExitApplication);
        RestartApplicationCommand = new RelayCommand(RestartApplication);
        ResetDiagnosticStatusText();
    }

#if UNIT_TESTS
    private sealed class TestOnlyApplicationErrorSink : IApplicationErrorSink
    {
        private TestOnlyApplicationErrorSink()
        {
        }

        public static TestOnlyApplicationErrorSink Shared { get; } = new();

        public Task ReportAsync(
            ApplicationError applicationError,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(applicationError);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
#endif

    public string PageTitleText => _getString("Nav.Settings");

    public string DescriptionText => _getString("Page.Settings.Description");

    /// <summary>Gets the localized generic message shown for unexpected command failures.</summary>
    public string UnexpectedErrorText => _getString("Application.UnexpectedError");

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

    public string UninstallText => _getString("Command.Uninstall");

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
        CultureInfo.CurrentCulture,
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

    private string _mihomoServiceDiagnosticText = string.Empty;

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

    private string _operationErrorText = string.Empty;

    private bool _isResetRecoveryRequired;

    public AsyncRelayCommand WindowsDiagnosticCommand { get; }

    public AsyncRelayCommand RefreshMihomoServiceStatusCommand { get; }

    public AsyncRelayCommand ApplyLaunchAtStartupCommand { get; }

    public AsyncRelayCommand RestartConnectionSamplingCommand { get; }

    public AsyncRelayCommand ApplyNetworkSettingsCommand { get; }

    public string OperationErrorText
    {
        get => _operationErrorText;
        private set
        {
            if (SetProperty(ref _operationErrorText, value))
            {
                OnPropertyChanged(nameof(HasOperationError));
            }
        }
    }

    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationErrorText);

    /// <summary>
    /// Gets whether a full settings reset could not be compensated and the process must restart
    /// before further in-process state can be trusted.
    /// </summary>
    public bool IsResetRecoveryRequired
    {
        get => _isResetRecoveryRequired;
        private set => SetProperty(ref _isResetRecoveryRequired, value);
    }

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

    public bool CanToggleTransparentProxy =>
        _mihomoServiceStatus.IsKnown
        && _mihomoServiceStatus.IsInstalled
        && string.IsNullOrEmpty(_mihomoServiceStatus.ProvisioningFailureCode);

    public string MihomoServiceStatusText
    {
        get => _mihomoServiceStatusText;
        private set => SetProperty(ref _mihomoServiceStatusText, value);
    }

    /// <summary>Gets the full stable diagnostic shown as the service-status tooltip.</summary>
    public string MihomoServiceDiagnosticText
    {
        get => _mihomoServiceDiagnosticText;
        private set => SetProperty(ref _mihomoServiceDiagnosticText, value);
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
        _appliedLaunchAtStartup = _settings.LaunchAtStartupEnabled;
        _pendingLaunchAtStartup = _appliedLaunchAtStartup;
        SetProperty(ref _launchAtStartupEnabled, _appliedLaunchAtStartup, nameof(LaunchAtStartupEnabled));
        RefreshMihomoServiceStatus();
        _appliedTransparentProxyEnabled = _settings.TransparentProxyEnabled;
        _appliedMixedPort = _settings.MixedPort;
        SetProperty(
            ref _transparentProxyEnabled,
            _appliedTransparentProxyEnabled,
            nameof(TransparentProxyEnabled));
        MixedPort = _appliedMixedPort;
        _appliedConnectionSamplingEnabled = _settings.ConnectionSamplingEnabled;
        _appliedConnectionSamplingIntervalSeconds = _settings.ConnectionSamplingIntervalSeconds;
        SetProperty(
            ref _connectionSamplingEnabled,
            _appliedConnectionSamplingEnabled,
            nameof(ConnectionSamplingEnabled));
        ConnectionSamplingIntervalSeconds = _appliedConnectionSamplingIntervalSeconds;
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
        catch (ArgumentException exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
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
        foreach ((AppLanguage language, string displayName) in _supportedLanguages)
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
            nameof(UninstallText),
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

        if (SetProperty(ref _transparentProxyEnabled, isEnabled, nameof(TransparentProxyEnabled)))
        {
            RequestNetworkSettingsApply();
        }
    }

    /// <summary>Refreshes the cached mihomo service status.</summary>
    private void RefreshMihomoServiceStatus()
    {
        SetMihomoServiceStatus(_mihomoServiceController.GetLatestStatus());
    }

    private async Task RefreshMihomoServiceStatusAsync(CancellationToken cancellationToken)
    {
        OperationErrorText = string.Empty;
        try
        {
            MihomoServiceStatus status = await _mihomoServiceController.RefreshStatusAsync(cancellationToken);
            SetMihomoServiceStatus(status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            OperationErrorText = _getString("Application.UnexpectedError");
            throw;
        }
    }

    /// <summary>Sets the cached mihomo service status and dependent bindable values.</summary>
    /// <param name="status">New service status.</param>
    private void SetMihomoServiceStatus(MihomoServiceStatus status)
    {
        _mihomoServiceStatus = status;
        MihomoServiceStatusText = string.IsNullOrWhiteSpace(status.Message)
            ? _getString("MihomoService.Status.Unknown")
            : status.Message;
        MihomoServiceDiagnosticText = RuntimeFailureDiagnostics.Format(
            status.ProvisioningFailureCode ?? status.IpcFailureCode,
            _getString,
            MihomoServiceStatusText);
        OnPropertyChanged(nameof(CanToggleTransparentProxy));
    }

    /// <summary>Persists the launch-at-startup switch and requests system registration sync.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetLaunchAtStartupEnabled(bool isEnabled)
    {
        _pendingLaunchAtStartup = isEnabled;
        _settings.LaunchAtStartupEnabled = isEnabled;
        SetProperty(ref _launchAtStartupEnabled, isEnabled, nameof(LaunchAtStartupEnabled));
        ApplyLaunchAtStartupCommand.Execute(null);
    }

    /// <summary>Applies the latest requested startup registration and coalesces changes made while an update is running.</summary>
    private async Task SynchronizeLaunchAtStartupAsync(CancellationToken cancellationToken)
    {
        OperationErrorText = string.Empty;
        while (true)
        {
            bool desiredState = _pendingLaunchAtStartup;
            try
            {
                await _applyLaunchAtStartupAsync(desiredState, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RestoreAppliedLaunchAtStartup();
                throw;
            }
            catch
            {
                RestoreAppliedLaunchAtStartup();
                OperationErrorText = _getString("Application.UnexpectedError");
                throw;
            }

            _appliedLaunchAtStartup = desiredState;
            if (desiredState == _pendingLaunchAtStartup)
            {
                return;
            }
        }
    }

    private void RestoreAppliedLaunchAtStartup()
    {
        _pendingLaunchAtStartup = _appliedLaunchAtStartup;
        _settings.LaunchAtStartupEnabled = _appliedLaunchAtStartup;
        SetProperty(ref _launchAtStartupEnabled, _appliedLaunchAtStartup, nameof(LaunchAtStartupEnabled));
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

        if (port != MixedPort)
        {
            MixedPort = port;
            RefreshProxyInformation();
            RequestNetworkSettingsApply();
        }

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
            CultureInfo.CurrentCulture,
            _getString("Settings.ProxyInformation.LocalEntry.Format"),
            MixedPort);
        ProxyCoreConfigurationText = string.Format(
            CultureInfo.CurrentCulture,
            _getString("Settings.ProxyInformation.CoreConfig.Format"),
            information.ConfigPath);
        ProxyCoreBinaryText = string.Format(
            CultureInfo.CurrentCulture,
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
        RequestConnectionSamplingRestart();
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
        RequestConnectionSamplingRestart();
        return true;
    }

    /// <summary>
    /// Reloads a transactionally activated package without claiming that restart-bound
    /// tray or regional resources were rebuilt in the current process.
    /// </summary>
    public void ReloadAfterDataImport()
    {
        RestartRequiredSettingsBaseline baseline = CaptureRestartRequiredSettingsBaseline();
        ReloadAfterSettingsReset(baseline);
    }

    /// <summary>Queues one coalesced runtime transaction for the latest requested TUN and port values.</summary>
    private void RequestNetworkSettingsApply()
    {
        Interlocked.Increment(ref _networkSettingsRevision);
        if (ApplyNetworkSettingsCommand.IsRunning
            && ApplyNetworkSettingsCommand.ExecutionTask is Task activeExecution)
        {
            if (_networkSettingsRequeueTask.IsCompleted)
            {
                _networkSettingsRequeueTask = RequeueNetworkSettingsApplyAsync(activeExecution);
            }

            return;
        }

        ApplyNetworkSettingsCommand.Execute(null);
    }

    /// <summary>Re-enters through the caller's UI context after the busy command releases itself.</summary>
    private async Task RequeueNetworkSettingsApplyAsync(Task activeExecution)
    {
        await activeExecution;
        if (Volatile.Read(ref _networkSettingsRevision)
                != Volatile.Read(ref _appliedNetworkSettingsRevision)
            && !ApplyNetworkSettingsCommand.IsRunning)
        {
            ApplyNetworkSettingsCommand.Execute(null);
        }
    }

    /// <summary>Applies the latest network preferences and restores the last verified request on failure.</summary>
    private async Task SynchronizeNetworkSettingsAsync(CancellationToken cancellationToken)
    {
        await _networkSettingsGate.WaitAsync(cancellationToken);
        try
        {
            OperationErrorText = string.Empty;
            while (true)
            {
                int requestedRevision = Volatile.Read(ref _networkSettingsRevision);
                bool desiredTransparentProxyEnabled = _transparentProxyEnabled;
                int desiredMixedPort = MixedPort;
                try
                {
                    await _applyNetworkSettingsAsync(
                            desiredTransparentProxyEnabled,
                            desiredMixedPort,
                            cancellationToken);
                    if (_settings.TransparentProxyEnabled != desiredTransparentProxyEnabled
                        || _settings.MixedPort != desiredMixedPort)
                    {
                        throw new InvalidOperationException(
                            "The verified network runtime did not commit its requested settings.");
                    }
                }
                catch (OperationCanceledException)
                {
                    ReloadCommittedNetworkSettings();
                    Volatile.Write(
                        ref _appliedNetworkSettingsRevision,
                        Volatile.Read(ref _networkSettingsRevision));
                    throw;
                }
                catch (Exception exception)
                {
                    ReloadCommittedNetworkSettings();
                    Volatile.Write(
                        ref _appliedNetworkSettingsRevision,
                        Volatile.Read(ref _networkSettingsRevision));
                    string fallbackMessage = _getString("Application.UnexpectedError");
                    OperationErrorText = RuntimeFailureDiagnostics.TryExtractCode(
                        exception,
                        out string? diagnosticCode)
                        ? RuntimeFailureDiagnostics.Format(
                            diagnosticCode,
                            _getString,
                            fallbackMessage)
                        : fallbackMessage;
                    throw;
                }

                _appliedTransparentProxyEnabled = desiredTransparentProxyEnabled;
                _appliedMixedPort = desiredMixedPort;
                Volatile.Write(ref _appliedNetworkSettingsRevision, requestedRevision);
                if (requestedRevision == Volatile.Read(ref _networkSettingsRevision))
                {
                    return;
                }
            }
        }
        finally
        {
            _networkSettingsGate.Release();
        }
    }

    /// <summary>
    /// Reloads the durable state chosen by the mutation coordinator. It deliberately does not
    /// overwrite the store: a recovery-required outcome may have committed either side of the
    /// transition, and the ViewModel must not invent a successful compensation.
    /// </summary>
    private void ReloadCommittedNetworkSettings()
    {
        _appliedTransparentProxyEnabled = _settings.TransparentProxyEnabled;
        _appliedMixedPort = _settings.MixedPort;
        SetProperty(
            ref _transparentProxyEnabled,
            _appliedTransparentProxyEnabled,
            nameof(TransparentProxyEnabled));
        MixedPort = _appliedMixedPort;
        RefreshProxyInformation();
    }

    /// <summary>Queues a coalesced sampling restart for the latest persisted sampling settings.</summary>
    private void RequestConnectionSamplingRestart()
    {
        Interlocked.Increment(ref _connectionSamplingRevision);
        RestartConnectionSamplingCommand.Execute(null);
    }

    /// <summary>Applies the latest sampling settings and restores the last applied values when the restart fails.</summary>
    private async Task SynchronizeConnectionSamplingAsync(CancellationToken cancellationToken)
    {
        OperationErrorText = string.Empty;
        while (true)
        {
            int requestedRevision = Volatile.Read(ref _connectionSamplingRevision);
            bool desiredEnabled = _settings.ConnectionSamplingEnabled;
            int desiredIntervalSeconds = _settings.ConnectionSamplingIntervalSeconds;

            try
            {
                await _restartConnectionSamplingAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RestoreAppliedConnectionSampling();
                throw;
            }
            catch
            {
                RestoreAppliedConnectionSampling();
                OperationErrorText = _getString("Application.UnexpectedError");
                throw;
            }

            _appliedConnectionSamplingEnabled = desiredEnabled;
            _appliedConnectionSamplingIntervalSeconds = desiredIntervalSeconds;
            if (requestedRevision == Volatile.Read(ref _connectionSamplingRevision))
            {
                return;
            }
        }
    }

    private void RestoreAppliedConnectionSampling()
    {
        _settings.ConnectionSamplingEnabled = _appliedConnectionSamplingEnabled;
        _settings.ConnectionSamplingIntervalSeconds = _appliedConnectionSamplingIntervalSeconds;
        SetProperty(
            ref _connectionSamplingEnabled,
            _appliedConnectionSamplingEnabled,
            nameof(ConnectionSamplingEnabled));
        ConnectionSamplingIntervalSeconds = _appliedConnectionSamplingIntervalSeconds;
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
        StartupRestoreFallbackStatusText = _getString(_isStartupRestoreFallbackRegistered()
            ? "Settings.StartupRestoreFallback.Status.Registered"
            : "Settings.StartupRestoreFallback.Status.NotRegistered");
    }

    /// <summary>Registers the startup restore fallback helper and refreshes status.</summary>
    public void RegisterStartupRestoreFallback()
    {
        _registerStartupRestoreFallback();
        RefreshStartupRestoreFallbackStatus();
    }

    /// <summary>Uninstalls the startup restore fallback helper and refreshes status.</summary>
    public void UninstallStartupRestoreFallback()
    {
        _uninstallStartupRestoreFallback();
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
                string.Format(CultureInfo.CurrentCulture, _getString("Settings.ConnectionTest.StatusHttp.Format"), statusCode),
                FormatLatency(stopwatch.Elapsed),
                (int)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (TaskCanceledException exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
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
        catch (Exception exception) when (
            exception is HttpRequestException or UriFormatException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            stopwatch.Stop();
            await ReportUnexpectedAsync("settings-connection-test-target", exception);
            return new ConnectionTestTargetResult(
                label,
                url,
                false,
                string.Format(
                    CultureInfo.CurrentCulture,
                    _getString("Settings.ConnectionTest.Failed.Format"),
                    _getString("Application.UnexpectedError")),
                FormatLatency(stopwatch.Elapsed),
                null);
        }
    }

    private void AppendConnectionTestLog(ConnectionTestReport report)
    {
        string level = report.SummaryState == ConnectionTestSummaryState.AllPassed ? "Info" : "Warning";
        string detail = string.Join(
            Environment.NewLine,
            report.Results.Select(result => $"{result.Label} | {result.StatusText} | {result.LatencyText}"));
        _appendLog(level, "ConnectionTest", report.SummaryText, detail);
    }

    private async Task ReportUnexpectedAsync(string operationName, Exception exception)
    {
        try
        {
            await _errorSink.ReportAsync(
                new ApplicationError(operationName, exception),
                CancellationToken.None);
        }
        catch (Exception sinkException) when (
            !ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
            // Preserve the generic UI result even when diagnostics are unavailable.
        }
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
        return string.Format(CultureInfo.CurrentCulture, _getString("Settings.ConnectionTest.PartialPassed.Format"), passed, results.Count);
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
        SetLaunchAtStartupEnabled(false);

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
        SetProperty(
            ref _transparentProxyEnabled,
            CanToggleTransparentProxy,
            nameof(TransparentProxyEnabled));
        MixedPort = DefaultMixedPort;
        RequestNetworkSettingsApply();

        _settings.ConnectionSamplingEnabled = true;
        SetProperty(ref _connectionSamplingEnabled, true, nameof(ConnectionSamplingEnabled));

        _settings.ConnectionSamplingIntervalSeconds = DefaultConnectionSamplingIntervalSeconds;
        ConnectionSamplingIntervalSeconds = DefaultConnectionSamplingIntervalSeconds;

        _settings.ConnectionTestUrl = DefaultConnectionTestUrl;
        ConnectionTestUrl = _settings.ConnectionTestUrl;
        ResetConnectionTestUrlsToDefaults();

        RequestConnectionSamplingRestart();
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

    /// <summary>
    /// Resets durable settings, then converges every external settings participant to the committed defaults.
    /// </summary>
    /// <remarks>
    /// Cancellation is honored only before the durable reset starts. Once the reset callback has been invoked,
    /// all activation and compensation work uses a non-cancelable token so caller lifetime cannot strand a
    /// partially applied reset.
    /// </remarks>
    public async Task ResetAllSettingsAsync(CancellationToken cancellationToken)
    {
        await _resetSettingsGate.WaitAsync(cancellationToken);
        bool durableResetStarted = false;
        RestartRequiredSettingsBaseline? restartRequiredBaseline = null;
        ISettingsDestructiveRuntimeScope? runtimeMutation = null;
        ISettingsResetTransactionReceipt? resetReceipt = null;
        try
        {
            await WaitForOutstandingRuntimeSettingsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            runtimeMutation = await _beginDestructiveRuntimeMutationAsync(cancellationToken);

            ExternalSettingsSnapshot baseline = CaptureExternalSettingsSnapshot();
            restartRequiredBaseline = CaptureRestartRequiredSettingsBaseline();
            durableResetStarted = true;
            try
            {
                resetReceipt = runtimeMutation.BeginResetSettings()
                    ?? throw new InvalidOperationException(
                        "The settings reset transaction did not return a receipt.");
            }
            catch (Exception resetFailure) when (!ExceptionGraphClassifier.IsProcessFatal(resetFailure))
            {
                // The maintenance callback can fail after removing one or more durable values. Treat
                // the values that remain readable as authoritative and converge every participant to
                // that state before reporting the incomplete reset.
                ExternalSettingsSnapshot partialCommit = CaptureExternalSettingsSnapshot();
                Exception? convergenceFailure = await TryApplyExternalSettingsSnapshotAsync(
                    partialCommit,
                    runtimeMutation);
                if (convergenceFailure is not null)
                {
                    throw EnterResetRecoveryState(resetFailure, convergenceFailure);
                }

                MarkExternalSettingsApplied(partialCommit);
                IsResetRecoveryRequired = false;
                OperationErrorText = _getString("Application.UnexpectedError");
                ExceptionDispatchInfo.Capture(resetFailure).Throw();
                throw;
            }

            ExternalSettingsSnapshot committedDefaults = CaptureExternalSettingsSnapshot();
            Exception? activationFailure = await TryApplyExternalSettingsSnapshotAsync(
                committedDefaults,
                runtimeMutation);
            if (activationFailure is null)
            {
                MarkExternalSettingsApplied(committedDefaults);
                IsResetRecoveryRequired = false;
                OperationErrorText = string.Empty;
                await CompleteResetReceiptWithRetryAsync(
                    resetReceipt.CommitAsync,
                    CancellationToken.None);
                return;
            }

            Exception? durableRollbackFailure = await TryCompleteResetReceiptWithRetryAsync(
                resetReceipt.RollbackAsync,
                CancellationToken.None);
            if (durableRollbackFailure is not null)
            {
                throw EnterResetRecoveryState(activationFailure, durableRollbackFailure);
            }

            Exception? compensationFailure = await TryRestoreExternalSettingsSnapshotAsync(
                baseline,
                runtimeMutation);
            if (compensationFailure is not null)
            {
                throw EnterResetRecoveryState(activationFailure, compensationFailure);
            }

            MarkExternalSettingsApplied(baseline);
            IsResetRecoveryRequired = false;
            OperationErrorText = _getString("Application.UnexpectedError");
            ExceptionDispatchInfo.Capture(activationFailure).Throw();
            throw new UnreachableException();
        }
        finally
        {
            try
            {
                if (resetReceipt is not null)
                {
                    await resetReceipt.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    if (runtimeMutation is not null)
                    {
                        await runtimeMutation.DisposeAsync();
                    }
                }
                finally
                {
                    try
                    {
                        if (durableResetStarted)
                        {
                            ReloadAfterSettingsReset(restartRequiredBaseline!.Value);
                        }
                    }
                    finally
                    {
                        _resetSettingsGate.Release();
                    }
                }
            }

        }
    }

    private static async Task CompleteResetReceiptWithRetryAsync(
        Func<CancellationToken, Task> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await completion(cancellationToken);
        }
        catch (Exception firstFailure) when (!ExceptionGraphClassifier.IsProcessFatal(firstFailure))
        {
            try
            {
                await completion(CancellationToken.None);
            }
            catch (Exception retryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(retryFailure))
            {
                throw new AggregateException(
                    "The retained settings reset decision could not be finalized after retry.",
                    firstFailure,
                    retryFailure);
            }
        }
    }

    private static async Task<Exception?> TryCompleteResetReceiptWithRetryAsync(
        Func<CancellationToken, Task> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await CompleteResetReceiptWithRetryAsync(completion, cancellationToken);
            return null;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return exception;
        }
    }

    /// <summary>Waits until view-model-owned runtime commands have quiesced before the reset commit point.</summary>
    private async Task WaitForOutstandingRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            List<Task> activeTasks = new[]
            {
                ApplyLaunchAtStartupCommand,
                RestartConnectionSamplingCommand,
                ApplyNetworkSettingsCommand,
            }
                .Where(static command => command.IsRunning && command.ExecutionTask is not null)
                .Select(static command => command.ExecutionTask!)
                .ToList();
            Task networkRequeueTask = _networkSettingsRequeueTask;
            if (!networkRequeueTask.IsCompleted)
            {
                activeTasks.Add(networkRequeueTask);
            }

            if (activeTasks.Count == 0)
            {
                return;
            }

            await Task.WhenAll(activeTasks).WaitAsync(cancellationToken);
        }
    }

    /// <summary>Captures durable values that have process-external or immediately visible participants.</summary>
    private ExternalSettingsSnapshot CaptureExternalSettingsSnapshot()
    {
        return new ExternalSettingsSnapshot(
            _settings.DisplayLanguage,
            _settings.AppThemeMode,
            _settings.AppAccentColorMode,
            _settings.AppAccentColorValue,
            _settings.LaunchAtStartupEnabled,
            _settings.ConnectionSamplingEnabled,
            _settings.ConnectionSamplingIntervalSeconds,
            _settings.CurrentMode,
            _settings.ActiveProfileId,
            _settings.TransparentProxyEnabled,
            _settings.MixedPort);
    }

    /// <summary>Applies every participant, collecting failures so one broken participant cannot hide another split.</summary>
    private async Task ApplyExternalSettingsSnapshotAsync(
        ExternalSettingsSnapshot snapshot,
        ISettingsDestructiveRuntimeScope runtimeMutation)
    {
        ArgumentNullException.ThrowIfNull(runtimeMutation);
        List<Exception> failures = [];
        CaptureParticipantFailure(
            () => _applyLanguage(snapshot.DisplayLanguage),
            failures);
        CaptureParticipantFailure(
            () => _applyTheme(snapshot.AppThemeMode),
            failures);
        CaptureParticipantFailure(
            () => _applyAccentColor(snapshot.AppAccentColorMode, snapshot.AppAccentColorValue),
            failures);
        await CaptureParticipantFailureAsync(
            () => runtimeMutation.ApplyLaunchAtStartupAsync(
                snapshot.LaunchAtStartupEnabled,
                CancellationToken.None),
            failures);
        await CaptureParticipantFailureAsync(
            () => runtimeMutation.RestartConnectionSamplingAsync(CancellationToken.None),
            failures);
        await CaptureParticipantFailureAsync(
            () => ApplyResetNetworkSettingsAsync(snapshot, runtimeMutation),
            failures);
        CaptureParticipantFailure(
            () => VerifyExternalSettingsSnapshot(snapshot),
            failures);

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "One or more settings reset participants failed to apply the durable state.",
                failures);
        }
    }

    private async Task ApplyResetNetworkSettingsAsync(
        ExternalSettingsSnapshot snapshot,
        ISettingsDestructiveRuntimeScope runtimeMutation)
    {
        await _networkSettingsGate.WaitAsync(CancellationToken.None);
        try
        {
            await runtimeMutation.ApplyNetworkSettingsAsync(
                snapshot.TransparentProxyEnabled,
                snapshot.MixedPort,
                CancellationToken.None);
        }
        finally
        {
            _networkSettingsGate.Release();
        }
    }

    private static void CaptureParticipantFailure(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }
    }

    private static async Task CaptureParticipantFailureAsync(
        Func<Task> action,
        ICollection<Exception> failures)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }
    }

    private async Task<Exception?> TryApplyExternalSettingsSnapshotAsync(
        ExternalSettingsSnapshot snapshot,
        ISettingsDestructiveRuntimeScope runtimeMutation)
    {
        try
        {
            await ApplyExternalSettingsSnapshotAsync(snapshot, runtimeMutation);
            return null;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return exception;
        }
    }

    private async Task<Exception?> TryRestoreExternalSettingsSnapshotAsync(
        ExternalSettingsSnapshot baseline,
        ISettingsDestructiveRuntimeScope runtimeMutation)
    {
        List<Exception> failures = [];
        CaptureParticipantFailure(
            () => runtimeMutation.RestoreDurableSettings(new SettingsExternalDurableSnapshot(
                baseline.DisplayLanguage,
                baseline.AppThemeMode,
                baseline.AppAccentColorMode,
                baseline.AppAccentColorValue,
                baseline.LaunchAtStartupEnabled,
                baseline.ConnectionSamplingEnabled,
                baseline.ConnectionSamplingIntervalSeconds,
                baseline.CurrentMode,
                baseline.ActiveProfileId,
                baseline.TransparentProxyEnabled,
                baseline.MixedPort)),
            failures);
        ExternalSettingsSnapshot durableTarget = CaptureExternalSettingsSnapshot();
        if (durableTarget != baseline)
        {
            failures.Add(new InvalidOperationException(
                "The retained reset receipt did not restore the previous durable settings."));
        }

        Exception? activationFailure = await TryApplyExternalSettingsSnapshotAsync(
            durableTarget,
            runtimeMutation);
        if (activationFailure is not null)
        {
            failures.Add(activationFailure);
        }

        if (durableTarget != baseline)
        {
            failures.Add(new InvalidOperationException(
                "The previous durable external settings could not be restored completely."));
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(
                "Settings reset compensation did not restore every participant.",
                failures),
        };
    }

    private void VerifyExternalSettingsSnapshot(ExternalSettingsSnapshot snapshot)
    {
        if (CaptureExternalSettingsSnapshot() != snapshot)
        {
            throw new InvalidOperationException(
                "A settings reset participant did not preserve the durable external settings snapshot.");
        }
    }

    private void MarkExternalSettingsApplied(ExternalSettingsSnapshot snapshot)
    {
        _appliedLaunchAtStartup = snapshot.LaunchAtStartupEnabled;
        _pendingLaunchAtStartup = snapshot.LaunchAtStartupEnabled;
        _appliedConnectionSamplingEnabled = snapshot.ConnectionSamplingEnabled;
        _appliedConnectionSamplingIntervalSeconds = snapshot.ConnectionSamplingIntervalSeconds;
        _appliedTransparentProxyEnabled = snapshot.TransparentProxyEnabled;
        _appliedMixedPort = snapshot.MixedPort;
        int networkRevision = Interlocked.Increment(ref _networkSettingsRevision);
        Volatile.Write(ref _appliedNetworkSettingsRevision, networkRevision);
    }

    private Exception EnterResetRecoveryState(Exception activationFailure, Exception compensationFailure)
    {
        IsResetRecoveryRequired = true;
        OperationErrorText = _getString("Application.UnexpectedError");
        List<Exception> failures = [activationFailure, compensationFailure];
        try
        {
            if (!_requestResetRecoveryRestart())
            {
                failures.Add(new InvalidOperationException(
                    "The mandatory restart request was rejected after settings reset compensation failed."));
            }
        }
        catch (Exception restartFailure) when (!ExceptionGraphClassifier.IsProcessFatal(restartFailure))
        {
            failures.Add(restartFailure);
        }

        return new AggregateException(
            "Settings reset could not converge or compensate every external participant; restart recovery is required.",
            failures);
    }

    private readonly record struct ExternalSettingsSnapshot(
        AppLanguage DisplayLanguage,
        AppThemeMode AppThemeMode,
        AppAccentColorMode AppAccentColorMode,
        string AppAccentColorValue,
        bool LaunchAtStartupEnabled,
        bool ConnectionSamplingEnabled,
        int ConnectionSamplingIntervalSeconds,
        ClashSharpMode CurrentMode,
        string ActiveProfileId,
        bool TransparentProxyEnabled,
        int MixedPort);

    /// <summary>
    /// Captures the process-applied baseline for settings whose existing UI resources are not rebuilt by
    /// the reset transaction. Reloading persisted values must not claim these values are already active.
    /// </summary>
    private RestartRequiredSettingsBaseline CaptureRestartRequiredSettingsBaseline()
    {
        return new RestartRequiredSettingsBaseline(
            _loadedTrayUseMonochromeInactiveIcon,
            _loadedMainlandChinaFeatureMode,
            _loadedMainlandChinaUrlBlockingEnabled);
    }

    private void ReloadAfterSettingsReset(RestartRequiredSettingsBaseline baseline)
    {
        Load();
        _loadedTrayUseMonochromeInactiveIcon = baseline.TrayUseMonochromeInactiveIcon;
        _loadedMainlandChinaFeatureMode = baseline.MainlandChinaFeatureMode;
        _loadedMainlandChinaUrlBlockingEnabled = baseline.MainlandChinaUrlBlockingEnabled;
        RaiseTrayIconRestartStateChanged();
        RaiseMainlandChinaRestartStateChanged();
        RaiseLocalizedTextChanges();
        RaiseSelectorBindingsChanged();
        ResetDiagnosticStatusText();
    }

    private readonly record struct RestartRequiredSettingsBaseline(
        bool TrayUseMonochromeInactiveIcon,
        MainlandChinaFeatureMode MainlandChinaFeatureMode,
        bool MainlandChinaUrlBlockingEnabled);

    private sealed class PassthroughDestructiveRuntimeScope(
        Func<bool, CancellationToken, Task> applyLaunchAtStartupAsync,
        Func<CancellationToken, Task> restartConnectionSamplingAsync,
        Func<bool, int, CancellationToken, Task> applyNetworkSettingsAsync,
        Func<ISettingsResetTransactionReceipt> beginResetSettings,
        Action<SettingsExternalDurableSnapshot> restoreDurableSettings)
        : ISettingsDestructiveRuntimeScope
    {
        public Task<ISettingsDataPackageTransactionReceipt> BeginImportAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<ISettingsDataPackageTransactionReceipt>(
                new NotSupportedException(
                    "The legacy settings runtime scope does not provide data-package imports."));
        }

        public ISettingsResetTransactionReceipt BeginResetSettings()
        {
            return beginResetSettings();
        }

        public void RestoreDurableSettings(SettingsExternalDurableSnapshot snapshot)
        {
            restoreDurableSettings(snapshot);
        }

        public Task ApplyLaunchAtStartupAsync(
            bool isEnabled,
            CancellationToken cancellationToken)
        {
            return applyLaunchAtStartupAsync(isEnabled, cancellationToken);
        }

        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken)
        {
            return restartConnectionSamplingAsync(cancellationToken);
        }

        public Task ApplyNetworkSettingsAsync(
            bool transparentProxyEnabled,
            int mixedPort,
            CancellationToken cancellationToken)
        {
            return applyNetworkSettingsAsync(
                transparentProxyEnabled,
                mixedPort,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LegacySettingsResetTransactionReceipt : ISettingsResetTransactionReceipt
    {
        public LegacySettingsResetTransactionReceipt(Action resetSettings)
        {
            ArgumentNullException.ThrowIfNull(resetSettings);
            resetSettings();
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Clears all local application data through the injected maintenance action and reloads the view model.</summary>
    public async Task ClearAllDataAsync(CancellationToken cancellationToken)
    {
        await _clearAllDataAsync(cancellationToken);
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
    public Task<IReadOnlyList<StartupConflictIssue>> CheckStartupConflictsAsync(
        CancellationToken cancellationToken)
    {
        return _checkStartupConflictsAsync(MixedPort, cancellationToken);
    }

    private void ReloadAfterMaintenance()
    {
        Load();
        RaiseLocalizedTextChanges();
        RaiseSelectorBindingsChanged();
        ResetDiagnosticStatusText();
    }

}
