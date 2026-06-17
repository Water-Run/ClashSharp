/*
 * Settings ViewModel
 * Owns settings state transitions for the settings page without depending on WinUI controls
 *
 * @author: WaterRun
 * @file: ViewModel/SettingsViewModel.cs
 * @date: 2026-06-17
 */

using System;
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

    bool TransparentProxyEnabled { get; set; }

    bool FallbackToSystemProxyWhenTunFails { get; set; }

    int MixedPort { get; set; }

    bool ConnectionSamplingEnabled { get; set; }

    int ConnectionSamplingIntervalSeconds { get; set; }

    bool CheckStaleProxyOnStartup { get; set; }

    bool RestoreProxyOnExit { get; set; }

    ProxyRecoveryMode ProxyRecoveryMode { get; set; }

    MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; }
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

    public bool FallbackToSystemProxyWhenTunFails
    {
        get => _settings.FallbackToSystemProxyWhenTunFails;
        set => _settings.FallbackToSystemProxyWhenTunFails = value;
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

    public ProxyRecoveryMode ProxyRecoveryMode
    {
        get => _settings.ProxyRecoveryMode;
        set => _settings.ProxyRecoveryMode = value;
    }

    public MainlandChinaFeatureMode MainlandChinaFeatureMode
    {
        get => _settings.MainlandChinaFeatureMode;
        set => _settings.MainlandChinaFeatureMode = value;
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
    /// <summary>Persistent settings store used by this view model.</summary>
    private readonly ISettingsStore _settings;

    /// <summary>Callback invoked when the display language changes.</summary>
    private readonly Action<AppLanguage> _applyLanguage;

    /// <summary>Callback invoked when background connection sampling settings change.</summary>
    private readonly Action _restartConnectionSampling;

    /// <summary>Localization resolver used by bindable settings labels.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Proxy information snapshot provider used by the proxy information card.</summary>
    private readonly Func<SettingsProxyInformation> _getProxyInformation;

    /// <summary>Diagnostics command router used by Windows-native diagnostic buttons.</summary>
    private readonly SettingsDiagnosticsViewModel? _diagnosticsViewModel;

    /// <summary>Initializes a new settings view model.</summary>
    /// <param name="settings">Settings store. Must not be null.</param>
    /// <param name="applyLanguage">Callback used to update the active UI language. Must not be null.</param>
    /// <param name="restartConnectionSampling">Callback used to restart connection sampling. Must not be null.</param>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling)
        : this(settings, applyLanguage, restartConnectionSampling, key => key)
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
        : this(settings, applyLanguage, restartConnectionSampling, getString, () => new SettingsProxyInformation(string.Empty, false, string.Empty))
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
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _applyLanguage = applyLanguage ?? throw new ArgumentNullException(nameof(applyLanguage));
        _restartConnectionSampling = restartConnectionSampling ?? throw new ArgumentNullException(nameof(restartConnectionSampling));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _getProxyInformation = getProxyInformation ?? throw new ArgumentNullException(nameof(getProxyInformation));
        _diagnosticsViewModel = diagnosticsViewModel;
        WindowsDiagnosticCommand = new AsyncRelayCommand(ExecuteWindowsDiagnosticCommandAsync);
        Load();
        ResetDiagnosticStatusText();
    }

    public string PageTitleText => _getString("Nav.Settings");

    public string DescriptionText => _getString("Page.Settings.Description");

    public string LanguageSectionTitleText => _getString("Settings.Section.Language");

    public string LanguageTitleText => _getString("Settings.Language.Title");

    public string LanguageDescriptionText => _getString("Settings.Language.Description");

    public string ProxySectionTitleText => _getString("Settings.Section.Proxy");

    public string TransparentProxyTitleText => _getString("Settings.TransparentProxy.Title");

    public string TransparentProxyDescriptionText => _getString("Settings.TransparentProxy.Description");

    public string TunFallbackTitleText => _getString("Settings.TunFallback.Title");

    public string TunFallbackDescriptionText => _getString("Settings.TunFallback.Description");

    public string MixedPortTitleText => _getString("Settings.MixedPort.Title");

    public string MixedPortDescriptionText => _getString("Settings.MixedPort.Description");

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

    public string WindowsNativeSectionTitleText => _getString("Settings.Section.WindowsNative");

    public string WindowsNativeTitleText => _getString("Settings.WindowsNative.Title");

    public string WindowsNativeDescriptionText => _getString("Settings.WindowsNative.Description");

    public string WslDiagnosticTitleText => _getString("Settings.Wsl.Title");

    public string TerminalDiagnosticTitleText => _getString("Settings.Terminal.Title");

    public string StoreDiagnosticTitleText => _getString("Settings.Store.Title");

    public string DiagnoseText => _getString("Command.Diagnose");

    public string ApplyText => _getString("Command.Apply");

    public string ResetText => _getString("Command.Reset");

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

    public string ProxyRecoveryModeTitleText => _getString("Settings.ProxyRecoveryMode.Title");

    public string ProxyRecoveryModeDescriptionText => _getString("Settings.ProxyRecoveryMode.Description");

    public string ProxyRecoveryIgnoreText => _getString("Settings.ProxyRecoveryMode.Ignore");

    public string ProxyRecoveryEnableText => _getString("Settings.ProxyRecoveryMode.Enable");

    public string ProxyRecoveryDisableText => _getString("Settings.ProxyRecoveryMode.Disable");

    public string MainlandChinaSectionTitleText => _getString("Settings.Section.MainlandChina");

    public string MainlandChinaDisplayTitleText => _getString("Settings.MainlandChinaDisplay.Title");

    public string MainlandChinaDisplayDescriptionText => _getString("Settings.MainlandChinaDisplay.Description");

    public string MainlandChinaDisabledText => _getString("Settings.MainlandChinaFeature.Disabled");

    public string MainlandChinaFlagOnlyText => _getString("Settings.MainlandChinaFeature.FlagOnly");

    public string MainlandChinaFlagAndTextText => _getString("Settings.MainlandChinaFeature.FlagAndText");

    public string MainlandChinaKeywordFilterText => _getString("Settings.MainlandChinaFeature.KeywordFilter");

    public string MainlandChinaAllText => _getString("Settings.MainlandChinaFeature.All");

    /// <summary>Backing field for <see cref="DisplayLanguage"/>.</summary>
    private AppLanguage _displayLanguage;

    /// <summary>Backing field for <see cref="TransparentProxyEnabled"/>.</summary>
    private bool _transparentProxyEnabled;

    /// <summary>Backing field for <see cref="FallbackToSystemProxyWhenTunFails"/>.</summary>
    private bool _fallbackToSystemProxyWhenTunFails;

    /// <summary>Backing field for <see cref="MixedPort"/>.</summary>
    private int _mixedPort;

    /// <summary>Backing field for <see cref="ConnectionSamplingEnabled"/>.</summary>
    private bool _connectionSamplingEnabled;

    /// <summary>Backing field for <see cref="ConnectionSamplingIntervalSeconds"/>.</summary>
    private int _connectionSamplingIntervalSeconds;

    /// <summary>Backing field for <see cref="CheckStaleProxyOnStartup"/>.</summary>
    private bool _checkStaleProxyOnStartup;

    /// <summary>Backing field for <see cref="RestoreProxyOnExit"/>.</summary>
    private bool _restoreProxyOnExit;

    /// <summary>Backing field for <see cref="ProxyRecoveryMode"/>.</summary>
    private ProxyRecoveryMode _proxyRecoveryMode;

    /// <summary>Backing field for <see cref="MainlandChinaFeatureMode"/>.</summary>
    private MainlandChinaFeatureMode _mainlandChinaFeatureMode;

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
        get => (int)DisplayLanguage;
        set => SetDisplayLanguageIndex(value);
    }

    public bool TransparentProxyEnabled
    {
        get => _transparentProxyEnabled;
        set => SetTransparentProxyEnabled(value);
    }

    public bool FallbackToSystemProxyWhenTunFails
    {
        get => _fallbackToSystemProxyWhenTunFails;
        set => SetFallbackToSystemProxyWhenTunFails(value);
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

    public ProxyRecoveryMode ProxyRecoveryMode
    {
        get => _proxyRecoveryMode;
        private set
        {
            if (SetProperty(ref _proxyRecoveryMode, value))
            {
                OnPropertyChanged(nameof(ProxyRecoveryModeIndex));
            }
        }
    }

    public int ProxyRecoveryModeIndex
    {
        get => (int)ProxyRecoveryMode;
        set => SetProxyRecoveryModeIndex(value);
    }

    public MainlandChinaFeatureMode MainlandChinaFeatureMode
    {
        get => _mainlandChinaFeatureMode;
        private set
        {
            if (SetProperty(ref _mainlandChinaFeatureMode, value))
            {
                OnPropertyChanged(nameof(MainlandChinaFeatureModeIndex));
            }
        }
    }

    public int MainlandChinaFeatureModeIndex
    {
        get => (int)MainlandChinaFeatureMode;
        set => SetMainlandChinaFeatureModeIndex(value);
    }

    /// <summary>Loads the latest persisted settings into the view model properties.</summary>
    public void Load()
    {
        DisplayLanguage = _settings.DisplayLanguage;
        SetProperty(ref _transparentProxyEnabled, _settings.TransparentProxyEnabled, nameof(TransparentProxyEnabled));
        SetProperty(ref _fallbackToSystemProxyWhenTunFails, _settings.FallbackToSystemProxyWhenTunFails, nameof(FallbackToSystemProxyWhenTunFails));
        MixedPort = _settings.MixedPort;
        SetProperty(ref _connectionSamplingEnabled, _settings.ConnectionSamplingEnabled, nameof(ConnectionSamplingEnabled));
        ConnectionSamplingIntervalSeconds = _settings.ConnectionSamplingIntervalSeconds;
        SetProperty(ref _checkStaleProxyOnStartup, _settings.CheckStaleProxyOnStartup, nameof(CheckStaleProxyOnStartup));
        SetProperty(ref _restoreProxyOnExit, _settings.RestoreProxyOnExit, nameof(RestoreProxyOnExit));
        ProxyRecoveryMode = _settings.ProxyRecoveryMode;
        MainlandChinaFeatureMode = _settings.MainlandChinaFeatureMode;
        RefreshProxyInformation();
    }

    /// <summary>Persists a display language selected by combo box index.</summary>
    /// <param name="index">Language enum index.</param>
    /// <returns>True when the language was valid and persisted; otherwise false.</returns>
    public bool SetDisplayLanguageIndex(int index)
    {
        if (!Enum.IsDefined((AppLanguage)index))
        {
            return false;
        }

        AppLanguage language = (AppLanguage)index;
        _settings.DisplayLanguage = language;
        DisplayLanguage = language;
        _applyLanguage(language);
        RaiseLocalizedTextChanges();
        RefreshProxyInformation();
        ResetDiagnosticStatusText();
        return true;
    }

    /// <summary>Raises property changes for all localized bindable text properties.</summary>
    private void RaiseLocalizedTextChanges()
    {
        string[] propertyNames =
        [
            nameof(PageTitleText),
            nameof(DescriptionText),
            nameof(LanguageSectionTitleText),
            nameof(LanguageTitleText),
            nameof(LanguageDescriptionText),
            nameof(ProxySectionTitleText),
            nameof(TransparentProxyTitleText),
            nameof(TransparentProxyDescriptionText),
            nameof(TunFallbackTitleText),
            nameof(TunFallbackDescriptionText),
            nameof(MixedPortTitleText),
            nameof(MixedPortDescriptionText),
            nameof(ProxyInformationTitleText),
            nameof(ProxyInformationDescriptionText),
            nameof(ProxyLocalEntryText),
            nameof(ProxyCoreConfigurationText),
            nameof(ProxyCoreBinaryText),
            nameof(ConnectionSamplingTitleText),
            nameof(ConnectionSamplingDescriptionText),
            nameof(SamplingIntervalTitleText),
            nameof(SamplingIntervalDescriptionText),
            nameof(WindowsNativeSectionTitleText),
            nameof(WindowsNativeTitleText),
            nameof(WindowsNativeDescriptionText),
            nameof(WslDiagnosticTitleText),
            nameof(TerminalDiagnosticTitleText),
            nameof(StoreDiagnosticTitleText),
            nameof(DiagnoseText),
            nameof(ApplyText),
            nameof(ResetText),
            nameof(DiagnosticNotRunText),
            nameof(WslDiagnosticStatusText),
            nameof(TerminalDiagnosticStatusText),
            nameof(StoreDiagnosticStatusText),
            nameof(CheckStaleProxyTitleText),
            nameof(CheckStaleProxyDescriptionText),
            nameof(RestoreProxyOnExitTitleText),
            nameof(RestoreProxyOnExitDescriptionText),
            nameof(ProxyRecoveryModeTitleText),
            nameof(ProxyRecoveryModeDescriptionText),
            nameof(ProxyRecoveryIgnoreText),
            nameof(ProxyRecoveryEnableText),
            nameof(ProxyRecoveryDisableText),
            nameof(MainlandChinaSectionTitleText),
            nameof(MainlandChinaDisplayTitleText),
            nameof(MainlandChinaDisplayDescriptionText),
            nameof(MainlandChinaDisabledText),
            nameof(MainlandChinaFlagOnlyText),
            nameof(MainlandChinaFlagAndTextText),
            nameof(MainlandChinaKeywordFilterText),
            nameof(MainlandChinaAllText),
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
        _settings.TransparentProxyEnabled = isEnabled;
        SetProperty(ref _transparentProxyEnabled, isEnabled, nameof(TransparentProxyEnabled));
    }

    /// <summary>Persists the TUN fallback switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetFallbackToSystemProxyWhenTunFails(bool isEnabled)
    {
        _settings.FallbackToSystemProxyWhenTunFails = isEnabled;
        SetProperty(ref _fallbackToSystemProxyWhenTunFails, isEnabled, nameof(FallbackToSystemProxyWhenTunFails));
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
        if (intervalSeconds is < 5 or > 3600)
        {
            return false;
        }

        _settings.ConnectionSamplingIntervalSeconds = intervalSeconds;
        ConnectionSamplingIntervalSeconds = intervalSeconds;
        _restartConnectionSampling();
        return true;
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

    /// <summary>Persists a proxy recovery mode selected by combo box index.</summary>
    /// <param name="index">Recovery mode enum index.</param>
    /// <returns>True when the index was valid and persisted; otherwise false.</returns>
    public bool SetProxyRecoveryModeIndex(int index)
    {
        if (!Enum.IsDefined((ProxyRecoveryMode)index))
        {
            return false;
        }

        ProxyRecoveryMode mode = (ProxyRecoveryMode)index;
        _settings.ProxyRecoveryMode = mode;
        ProxyRecoveryMode = mode;
        return true;
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
        _settings.MainlandChinaFeatureMode = mode;
        MainlandChinaFeatureMode = mode;
        return true;
    }
}
