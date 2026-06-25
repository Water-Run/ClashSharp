/*
 * Master Control ViewModel
 * Owns bindable state and commands for the master control page
 *
 * @author: WaterRun
 * @file: ViewModel/MasterControlViewModel.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Localization contract required by <see cref="MasterControlViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations return a non-null string for every requested key.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: None required by the contract.
/// </remarks>
internal interface IMasterControlLocalization
{
    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Resolved localized string, or a fallback string when the key is unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    string GetString(string key);
}

/// <summary>Core runtime contract required by <see cref="MasterControlViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations return a non-empty version string when the bundled core is available.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: Implementations may start a short-lived core probe process.
/// </remarks>
internal interface IMasterControlCore
{
    /// <summary>Gets the bundled core version text.</summary>
    /// <param name="cancellationToken">Cancels the probe operation when requested.</param>
    /// <returns>The first user-facing version line returned by the core.</returns>
    /// <exception cref="FileNotFoundException">The bundled core binary is missing.</exception>
    /// <exception cref="InvalidOperationException">The core version probe fails.</exception>
    /// <remarks>
    /// Cancellation semantics: Implementations should stop only the version probe.
    /// Completion semantics: Does not mutate the long-running core state.
    /// </remarks>
    Task<string> GetVersionTextAsync(CancellationToken cancellationToken);
}

/// <summary>Windows proxy state contract required by <see cref="MasterControlViewModel"/>.</summary>
/// <remarks>
/// Invariants: Returned proxy state contains a non-null server string.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: Reads Windows proxy state.
/// </remarks>
internal interface IMasterControlWindowsProxy
{
    /// <summary>Gets current Windows system proxy state.</summary>
    /// <returns>Current Windows proxy state.</returns>
    /// <exception cref="InvalidOperationException">The proxy state cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The proxy registry state cannot be accessed.</exception>
    WindowsProxyState GetCurrentState();
}

/// <summary>Settings contract required by <see cref="MasterControlViewModel"/>.</summary>
/// <remarks>
/// Invariants: Current mode is a valid <see cref="ClashSharpMode"/> value.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: Setters may persist settings to durable storage.
/// </remarks>
internal interface IMasterControlSettings
{
    /// <summary>Gets or sets the current master takeover mode.</summary>
    /// <value>Current persisted mode.</value>
    ClashSharpMode CurrentMode { get; set; }

    /// <summary>Gets or sets whether transparent proxy is enabled in settings.</summary>
    /// <value>True when transparent proxy is enabled; otherwise false.</value>
    bool TransparentProxyEnabled { get; set; }

    /// <summary>Gets or sets whether Clash# launches when the user signs in.</summary>
    bool LaunchAtStartupEnabled { get; set; }

    /// <summary>Gets or sets whether background connection sampling is enabled.</summary>
    bool ConnectionSamplingEnabled { get; set; }

    /// <summary>Gets or sets whether marked URL blocking is enabled.</summary>
    bool MainlandChinaUrlBlockingEnabled { get; set; }

    /// <summary>Gets the active profile identifier.</summary>
    string ActiveProfileId { get; }

    /// <summary>Gets the local mixed proxy port.</summary>
    int MixedPort { get; }

    /// <summary>Gets the first proxy connection-test URL.</summary>
    string ConnectionTestProxyUrl1 { get; }

    /// <summary>Gets the second proxy connection-test URL.</summary>
    string ConnectionTestProxyUrl2 { get; }

    /// <summary>Gets the direct connection-test URL.</summary>
    string ConnectionTestDirectUrl { get; }
}

/// <summary>Page-level action requested by a functional master-control tile.</summary>
internal enum MasterControlTileAction
{
    ShowStartupPrompt,
    CheckStartupConflicts,
    RunLatencyTest,
    ExportConfiguration,
    ImportConfiguration,
}

/// <summary>Network takeover contract required by <see cref="MasterControlViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations either return the applied mode result or throw an expected runtime exception.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May start or stop the core and mutate Windows proxy state.
/// </remarks>
internal interface IMasterControlTakeover
{
    /// <summary>Applies a master takeover mode.</summary>
    /// <param name="mode">Mode to apply.</param>
    /// <returns>Result describing the applied runtime state.</returns>
    /// <exception cref="FileNotFoundException">Required runtime files are missing.</exception>
    /// <exception cref="InvalidOperationException">Runtime state cannot be applied.</exception>
    /// <exception cref="Win32Exception">Windows rejects proxy notification.</exception>
    /// <exception cref="UnauthorizedAccessException">Windows proxy state cannot be changed.</exception>
    NetworkTakeoverResult ApplyMode(ClashSharpMode mode);
}

/// <summary>Logging contract required by <see cref="MasterControlViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations persist or discard each complete log entry atomically.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May write to persistent log storage.
/// </remarks>
internal interface IMasterControlLog
{
    /// <summary>Appends one log entry.</summary>
    /// <param name="level">Log level. Must not be null.</param>
    /// <param name="category">Log category. Must not be null.</param>
    /// <param name="message">Log summary. Must not be null.</param>
    /// <param name="detail">Optional detail text; null when no detail exists.</param>
    /// <exception cref="ArgumentNullException"><paramref name="level"/>, <paramref name="category"/>, or <paramref name="message"/> is null.</exception>
    void Append(string level, string category, string message, string? detail);
}

/// <summary>Tray status contract required by the master-control page.</summary>
internal interface IMasterControlTrayStatus
{
    /// <summary>Gets current node and latency status.</summary>
    TrayStatusSnapshot GetSnapshot();
}

/// <summary>Fallback tray status provider used in tests and when runtime status is unavailable.</summary>
internal sealed class UnavailableMasterControlTrayStatus : IMasterControlTrayStatus
{
    public static UnavailableMasterControlTrayStatus Instance { get; } = new();

    public TrayStatusSnapshot GetSnapshot()
    {
        return TrayStatusSnapshot.Unavailable;
    }
}

/// <summary>One draggable master-control information tile.</summary>
internal sealed class MasterControlInfoTileViewModel : ObservableObject
{
    private string _value;
    private string _detail;
    private bool _isVisible = true;
    private bool _isToggleOn;

    public MasterControlInfoTileViewModel(
        string id,
        string title,
        string value,
        string detail,
        string glyph,
        string description,
        string typeText,
        bool isToggleVisible = false,
        bool isToggleOn = false,
        RelayCommand? tileCommand = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        _value = value ?? throw new ArgumentNullException(nameof(value));
        _detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Glyph = glyph ?? throw new ArgumentNullException(nameof(glyph));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        TypeText = typeText ?? throw new ArgumentNullException(nameof(typeText));
        IsToggleVisible = isToggleVisible;
        _isToggleOn = isToggleOn;
        TileCommand = tileCommand;
    }

    public string Id { get; }

    public string Title { get; }

    public string Glyph { get; }

    public string Description { get; }

    public string TypeText { get; }

    public bool IsToggleVisible { get; }

    public RelayCommand? TileCommand { get; }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool IsToggleOn
    {
        get => _isToggleOn;
        set => SetProperty(ref _isToggleOn, value);
    }
}

/// <summary>Bindable view model for the master control page.</summary>
/// <remarks>
/// Invariants: Exactly one primary mode flag is true when <see cref="SelectedMode"/> is not faulted.
/// Thread safety: Not thread-safe; intended for UI-thread binding and command execution.
/// Side effects: Commands call injected services that can mutate runtime proxy and core state.
/// </remarks>
internal sealed class MasterControlViewModel : ObservableObject
{
    /// <summary>Localization provider used by visible text.</summary>
    private readonly IMasterControlLocalization _localization;

    /// <summary>Core service used for version probing.</summary>
    private readonly IMasterControlCore _core;

    /// <summary>Windows proxy service used for current state reads.</summary>
    private readonly IMasterControlWindowsProxy _windowsProxy;

    /// <summary>Settings store used for persisted mode and transparent-proxy state.</summary>
    private readonly IMasterControlSettings _settings;

    /// <summary>Takeover service used to apply selected modes.</summary>
    private readonly IMasterControlTakeover _takeover;

    /// <summary>Log sink used by mode application.</summary>
    private readonly IMasterControlLog _log;

    /// <summary>Tray status provider used for current node and latency details.</summary>
    private readonly IMasterControlTrayStatus _trayStatus;

    /// <summary>Shared application action dispatcher used by functional tiles.</summary>
    private readonly IApplicationActionDispatcher _actions;

    /// <summary>Callback invoked after a runtime mode is successfully applied.</summary>
    private readonly Func<ClashSharpMode, Task> _modeApplied;

    /// <summary>Backing field for <see cref="SelectedMode"/>.</summary>
    private ClashSharpMode _selectedMode;

    /// <summary>Backing field for <see cref="CoreStatusText"/>.</summary>
    private string _coreStatusText = string.Empty;

    /// <summary>Backing field for <see cref="SystemProxyStatusText"/>.</summary>
    private string _systemProxyStatusText = string.Empty;

    /// <summary>Backing field for <see cref="TransparentProxyStatusText"/>.</summary>
    private string _transparentProxyStatusText = string.Empty;

    /// <summary>Backing field for <see cref="CurrentNodeText"/>.</summary>
    private string _currentNodeText = string.Empty;

    /// <summary>Backing field for <see cref="LatencySummaryText"/>.</summary>
    private string _latencySummaryText = string.Empty;

    /// <summary>Whether the bundled core was available during the latest status refresh.</summary>
    private bool _isCoreAvailable = true;

    /// <summary>Information tiles displayed in the lower grid.</summary>
    private readonly ObservableCollection<MasterControlInfoTileViewModel> _infoTiles = [];

    /// <summary>Initializes a master control view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="core">Core runtime provider. Must not be null.</param>
    /// <param name="windowsProxy">Windows proxy provider. Must not be null.</param>
    /// <param name="settings">Settings store. Must not be null.</param>
    /// <param name="takeover">Network takeover provider. Must not be null.</param>
    /// <param name="log">Log sink. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public MasterControlViewModel(
        IMasterControlLocalization localization,
        IMasterControlCore core,
        IMasterControlWindowsProxy windowsProxy,
        IMasterControlSettings settings,
        IMasterControlTakeover takeover,
        IMasterControlLog log,
        IMasterControlTrayStatus? trayStatus = null,
        IApplicationActionDispatcher? actions = null,
        Func<ClashSharpMode, Task>? modeApplied = null)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _trayStatus = trayStatus ?? UnavailableMasterControlTrayStatus.Instance;
        _actions = actions ?? NoMasterControlApplicationActionDispatcher.Instance;
        _modeApplied = modeApplied ?? (_ => Task.CompletedTask);
        _selectedMode = _settings.CurrentMode;

        DisabledModeCommand = new AsyncRelayCommand(token => ApplyModeAsync(ClashSharpMode.Disabled, token));
        StandbyModeCommand = new AsyncRelayCommand(token => ApplyModeAsync(ClashSharpMode.Standby, token));
        RuleTakeoverModeCommand = new AsyncRelayCommand(token => ApplyModeAsync(ClashSharpMode.RuleTakeover, token));
        FullTakeoverModeCommand = new AsyncRelayCommand(token => ApplyModeAsync(ClashSharpMode.FullTakeover, token));
        LoadCommand = new AsyncRelayCommand(LoadAsync);

        CoreStatusText = string.Empty;
        SystemProxyStatusText = string.Empty;
        TransparentProxyStatusText = string.Empty;
        CurrentNodeText = _localization.GetString("Master.Status.CurrentNodeUnavailable");
        LatencySummaryText = _localization.GetString("Master.Status.LatencyUnavailable");
        BuildInfoTiles();
        RefreshTileValues();
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title; never null.</value>
    public string PageTitleText => _localization.GetString("Nav.MasterControl");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description; never null.</value>
    public string DescriptionText => _localization.GetString("Page.MasterControl.Description");

    /// <summary>Gets the status-control section title.</summary>
    /// <value>Localized section title; never null.</value>
    public string StatusControlTitleText => _localization.GetString("Master.StatusControl.Title");

    /// <summary>Gets the status-control section description.</summary>
    /// <value>Localized section description; never null.</value>
    public string StatusControlDescriptionText => _localization.GetString("Master.StatusControl.Description");

    /// <summary>Gets the disabled-mode title.</summary>
    /// <value>Localized mode title; never null.</value>
    public string DisabledModeTitleText => _localization.GetString("Master.Mode.Disabled.Title");

    /// <summary>Gets the disabled-mode description.</summary>
    /// <value>Localized mode description; never null.</value>
    public string DisabledModeDescriptionText => _localization.GetString("Master.Mode.Disabled.Description");

    /// <summary>Gets the standby-mode title.</summary>
    /// <value>Localized mode title; never null.</value>
    public string StandbyModeTitleText => _localization.GetString("Master.Mode.Standby.Title");

    /// <summary>Gets the standby-mode description.</summary>
    /// <value>Localized mode description; never null.</value>
    public string StandbyModeDescriptionText => _localization.GetString("Master.Mode.Standby.Description");

    /// <summary>Gets the rule-takeover mode title.</summary>
    /// <value>Localized mode title; never null.</value>
    public string RuleTakeoverModeTitleText => _localization.GetString("Master.Mode.RuleTakeover.Title");

    /// <summary>Gets the rule-takeover mode description.</summary>
    /// <value>Localized mode description; never null.</value>
    public string RuleTakeoverModeDescriptionText => _localization.GetString("Master.Mode.RuleTakeover.Description");

    /// <summary>Gets the full-takeover mode title.</summary>
    /// <value>Localized mode title; never null.</value>
    public string FullTakeoverModeTitleText => _localization.GetString("Master.Mode.FullTakeover.Title");

    /// <summary>Gets the full-takeover mode description.</summary>
    /// <value>Localized mode description; never null.</value>
    public string FullTakeoverModeDescriptionText => _localization.GetString("Master.Mode.FullTakeover.Description");

    /// <summary>Gets the core status card title.</summary>
    /// <value>Localized status title; never null.</value>
    public string CoreStatusTitleText => _localization.GetString("Master.Status.Core");

    /// <summary>Gets the system-proxy status card title.</summary>
    /// <value>Localized status title; never null.</value>
    public string SystemProxyTitleText => _localization.GetString("Master.Status.SystemProxy");

    /// <summary>Gets the transparent-proxy status card title.</summary>
    /// <value>Localized status title; never null.</value>
    public string TransparentProxyTitleText => _localization.GetString("Master.Status.TransparentProxy");

    /// <summary>Gets compact basic status text for the redesigned control header.</summary>
    public string BasicStatusText
    {
        get
        {
            if (!_isCoreAvailable || SelectedMode == ClashSharpMode.Faulted)
            {
                return _localization.GetString("Master.BasicStatus.Unavailable");
            }

            return SelectedMode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover
                ? _localization.GetString("Master.BasicStatus.Active")
                : _localization.GetString("Master.BasicStatus.Ready");
        }
    }

    public string CurrentNodeTitleText => _localization.GetString("Tray.Status.Node.Format").Replace("{0}", string.Empty, StringComparison.Ordinal).Trim();

    public string LatencyTitleText => _localization.GetString("Master.Tile.Latency");

    public string EditInfoTilesText => _localization.GetString("Master.Tile.Edit");

    public string SearchInfoTilesPlaceholderText => _localization.GetString("Master.Tile.SearchPlaceholder");

    public string VisibleTileText => _localization.GetString("Master.Tile.Visible");

    public IReadOnlyList<MasterControlInfoTileViewModel> InfoTiles => _infoTiles;

    /// <summary>Raised when a functional information tile requests page-level UI work.</summary>
    public event EventHandler<MasterControlTileAction>? TileActionRequested;

    /// <summary>Gets the selected takeover mode.</summary>
    /// <value>Current selected mode, including faulted state when application fails.</value>
    public ClashSharpMode SelectedMode
    {
        get => _selectedMode;
        private set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(IsDisabledModeSelected));
                OnPropertyChanged(nameof(IsStandbyModeSelected));
                OnPropertyChanged(nameof(IsRuleTakeoverModeSelected));
                OnPropertyChanged(nameof(IsFullTakeoverModeSelected));
                OnPropertyChanged(nameof(BasicStatusText));
                RefreshTileValues();
            }
        }
    }

    /// <summary>Gets whether the disabled mode is selected.</summary>
    /// <value>True when <see cref="SelectedMode"/> is <see cref="ClashSharpMode.Disabled"/>.</value>
    public bool IsDisabledModeSelected => SelectedMode == ClashSharpMode.Disabled;

    /// <summary>Gets whether the standby mode is selected.</summary>
    /// <value>True when <see cref="SelectedMode"/> is <see cref="ClashSharpMode.Standby"/>.</value>
    public bool IsStandbyModeSelected => SelectedMode == ClashSharpMode.Standby;

    /// <summary>Gets whether the rule-takeover mode is selected.</summary>
    /// <value>True when <see cref="SelectedMode"/> is <see cref="ClashSharpMode.RuleTakeover"/>.</value>
    public bool IsRuleTakeoverModeSelected => SelectedMode == ClashSharpMode.RuleTakeover;

    /// <summary>Gets whether the full-takeover mode is selected.</summary>
    /// <value>True when <see cref="SelectedMode"/> is <see cref="ClashSharpMode.FullTakeover"/>.</value>
    public bool IsFullTakeoverModeSelected => SelectedMode == ClashSharpMode.FullTakeover;

    /// <summary>Gets the visible core status.</summary>
    /// <value>User-facing status text; may be empty before loading.</value>
    public string CoreStatusText
    {
        get => _coreStatusText;
        private set => SetProperty(ref _coreStatusText, value);
    }

    /// <summary>Gets the visible Windows system proxy status.</summary>
    /// <value>User-facing status text; may be empty before loading.</value>
    public string SystemProxyStatusText
    {
        get => _systemProxyStatusText;
        private set => SetProperty(ref _systemProxyStatusText, value);
    }

    /// <summary>Gets the visible transparent proxy status.</summary>
    /// <value>User-facing status text; may be empty before loading.</value>
    public string TransparentProxyStatusText
    {
        get => _transparentProxyStatusText;
        private set => SetProperty(ref _transparentProxyStatusText, value);
    }

    public string CurrentNodeText
    {
        get => _currentNodeText;
        private set => SetProperty(ref _currentNodeText, value);
    }

    public string LatencySummaryText
    {
        get => _latencySummaryText;
        private set => SetProperty(ref _latencySummaryText, value);
    }

    /// <summary>Gets the command that loads runtime status.</summary>
    /// <value>Asynchronous load command.</value>
    public AsyncRelayCommand LoadCommand { get; }

    /// <summary>Gets the command that applies disabled mode.</summary>
    /// <value>Asynchronous mode command.</value>
    public AsyncRelayCommand DisabledModeCommand { get; }

    /// <summary>Gets the command that applies standby mode.</summary>
    /// <value>Asynchronous mode command.</value>
    public AsyncRelayCommand StandbyModeCommand { get; }

    /// <summary>Gets the command that applies rule-takeover mode.</summary>
    /// <value>Asynchronous mode command.</value>
    public AsyncRelayCommand RuleTakeoverModeCommand { get; }

    /// <summary>Gets the command that applies full-takeover mode.</summary>
    /// <value>Asynchronous mode command.</value>
    public AsyncRelayCommand FullTakeoverModeCommand { get; }

    /// <summary>Loads core and proxy status for the page.</summary>
    /// <param name="cancellationToken">Cancels the core version probe when requested.</param>
    /// <returns>A task that completes after status text is refreshed.</returns>
    /// <remarks>
    /// Cancellation semantics: Cancellation propagates from the core version probe.
    /// Thread / reentrancy: Not guarded; callers should use <see cref="LoadCommand"/> for UI invocation.
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string versionText = CoreVersionDisplayFormatter.Format(await _core.GetVersionTextAsync(cancellationToken));
            CoreStatusText = string.Format(
                _localization.GetString("Master.Status.CoreReady.Format"),
                versionText);
            _isCoreAvailable = true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException)
        {
            CoreStatusText = _localization.GetString("Master.Status.CoreUnavailable");
            _isCoreAvailable = false;
        }

        RefreshProxyStatus();
        RefreshTrayStatus();
        OnPropertyChanged(nameof(BasicStatusText));
        RefreshTileValues();
    }

    /// <summary>Applies a selected takeover mode and refreshes visible status.</summary>
    /// <param name="mode">Mode to apply.</param>
    /// <param name="cancellationToken">Cancellation token accepted for command consistency; not observed by the synchronous takeover service.</param>
    /// <returns>A completed task after the synchronous mode transition finishes.</returns>
    /// <remarks>
    /// Cancellation semantics: Cancellation is accepted for command-shape consistency but does not cancel synchronous mode application.
    /// Thread / reentrancy: Not guarded; callers should use mode commands for UI invocation.
    /// </remarks>
    public Task ApplyModeAsync(ClashSharpMode mode, CancellationToken cancellationToken)
    {
        try
        {
            NetworkTakeoverResult result = _takeover.ApplyMode(mode);
            SelectedMode = mode;
            _settings.CurrentMode = mode;
            CoreStatusText = result.CoreRunning
                ? _localization.GetString("Master.Status.Running")
                : _localization.GetString("Master.Status.NotRunning");
            SystemProxyStatusText = result.SystemProxyEnabled
                ? _localization.GetString("Master.Status.On")
                : _localization.GetString("Master.Status.Off");
            TransparentProxyStatusText = ResolveTransparentProxyStatus(result.TransparentProxyEnabled);
            _log.Append("Info", "MasterControl", result.Message, null);
            _ = _modeApplied(result.Mode);
            _isCoreAvailable = true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            SelectedMode = ClashSharpMode.Faulted;
            CoreStatusText = _localization.GetString("Master.Status.CoreStartFailed");
            _isCoreAvailable = false;
            _log.Append("Error", "MasterControl", "Failed to apply selected Clash# mode.", exception.Message);
        }

        OnPropertyChanged(nameof(BasicStatusText));
        RefreshTileValues();
        return Task.CompletedTask;
    }

    /// <summary>Refreshes visible proxy and transparent-proxy status from current service state.</summary>
    private void RefreshProxyStatus()
    {
        try
        {
            WindowsProxyState proxyState = _windowsProxy.GetCurrentState();
            SystemProxyStatusText = proxyState.IsEnabled
                ? _localization.GetString("Master.Status.On")
                : _localization.GetString("Master.Status.Off");
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            SystemProxyStatusText = _localization.GetString("Master.Status.Unavailable");
        }

        TransparentProxyStatusText = _settings.TransparentProxyEnabled
            ? _localization.GetString("Master.Status.Standby")
            : _localization.GetString("Master.Status.Off");
    }

    private void RefreshTrayStatus()
    {
        TrayStatusSnapshot snapshot = _trayStatus.GetSnapshot();
        CurrentNodeText = string.IsNullOrWhiteSpace(snapshot.CurrentNodeName)
            ? _localization.GetString("Master.Status.CurrentNodeUnavailable")
            : snapshot.CurrentNodeName;
        LatencySummaryText = snapshot.LatencyMilliseconds is int latency
            ? string.Format(_localization.GetString("Master.Status.Latency.Format"), latency)
            : _localization.GetString("Master.Status.LatencyUnavailable");
    }

    private void BuildInfoTiles()
    {
        _infoTiles.Clear();
        foreach (MasterTileDefinition tile in MasterTileCatalog.Create(this))
        {
            _infoTiles.Add(new MasterControlInfoTileViewModel(
                tile.Id,
                tile.Title,
                string.Empty,
                string.Empty,
                tile.Glyph,
                tile.Description,
                tile.TypeText,
                tile.IsToggleVisible,
                tile.IsToggleOn,
                tile.Command));
        }
    }

    private void RefreshTileValues()
    {
        SetTile("core", CoreStatusText, BasicStatusText);
        SetTile("mihomo-version", CoreStatusText, BasicStatusText);
        SetTile("system-proxy", SystemProxyStatusText, string.Empty);
        SetTile("transparent-proxy", TransparentProxyStatusText, string.Empty, _settings.TransparentProxyEnabled);
        SetTile("latency", LatencySummaryText, CurrentNodeText);
        SetTile("startup-launch", _settings.LaunchAtStartupEnabled
            ? _localization.GetString("Master.Status.StartupLaunchOn")
            : _localization.GetString("Master.Status.StartupLaunchOff"), string.Empty, _settings.LaunchAtStartupEnabled);
        SetTile("connection-sampling", _settings.ConnectionSamplingEnabled
            ? _localization.GetString("Master.Status.On")
            : _localization.GetString("Master.Status.Off"), string.Empty, _settings.ConnectionSamplingEnabled);
        SetTile("blocked-url", _settings.MainlandChinaUrlBlockingEnabled
            ? _localization.GetString("Master.Status.On")
            : _localization.GetString("Master.Status.Off"), string.Empty, _settings.MainlandChinaUrlBlockingEnabled);
        SetTile("active-profile", _settings.ActiveProfileId, string.Empty);
        SetTile("port", _settings.MixedPort.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty);
        SetTile("connection-test", "3", _localization.GetString("Master.Tile.ConnectionTest"));
        SetTile("connection-test-proxy-url-1", CompactUrl(_settings.ConnectionTestProxyUrl1), _settings.ConnectionTestProxyUrl1);
        SetTile("connection-test-proxy-url-2", CompactUrl(_settings.ConnectionTestProxyUrl2), _settings.ConnectionTestProxyUrl2);
        SetTile("connection-test-direct-url", CompactUrl(_settings.ConnectionTestDirectUrl), _settings.ConnectionTestDirectUrl);
        SetTile("startup-prompt", _localization.GetString("Settings.StartupGuide.ShowNow"), string.Empty);
        SetTile("startup-conflicts", _localization.GetString("Settings.CheckStartupConflicts.Now"), string.Empty);
        SetTile("export-config", _localization.GetString("Command.Export"), string.Empty);
        SetTile("import-config", _localization.GetString("Command.Import"), string.Empty);
    }

    private string TileDescription(string key)
    {
        return _localization.GetString($"Master.Tile.Description.{key}");
    }

    private void SetTile(string id, string value, string detail, bool? toggleOn = null)
    {
        foreach (MasterControlInfoTileViewModel tile in _infoTiles)
        {
            if (!StringComparer.Ordinal.Equals(tile.Id, id))
            {
                continue;
            }

            tile.Value = value;
            tile.Detail = detail;
            if (toggleOn is bool isToggleOn)
            {
                tile.IsToggleOn = isToggleOn;
            }

            return;
        }
    }

    private void ToggleTransparentProxy()
    {
        bool nextValue = !_settings.TransparentProxyEnabled;
        _settings.TransparentProxyEnabled = nextValue;
        _ = _actions.DispatchAsync(ApplicationActionKind.SetTransparentProxy, nextValue.ToString(), CancellationToken.None);
        TransparentProxyStatusText = _settings.TransparentProxyEnabled
            ? _localization.GetString("Master.Status.Standby")
            : _localization.GetString("Master.Status.Off");
        RefreshTileValues();
    }

    private void ToggleStartupLaunch()
    {
        bool nextValue = !_settings.LaunchAtStartupEnabled;
        _settings.LaunchAtStartupEnabled = nextValue;
        _ = _actions.DispatchAsync(ApplicationActionKind.SetLaunchAtStartup, nextValue.ToString(), CancellationToken.None);
        RefreshTileValues();
    }

    private void ToggleConnectionSampling()
    {
        bool nextValue = !_settings.ConnectionSamplingEnabled;
        _settings.ConnectionSamplingEnabled = nextValue;
        _ = _actions.DispatchAsync(ApplicationActionKind.SetConnectionSampling, nextValue.ToString(), CancellationToken.None);
        RefreshTileValues();
    }

    private void ToggleUrlBlocking()
    {
        bool nextValue = !_settings.MainlandChinaUrlBlockingEnabled;
        _settings.MainlandChinaUrlBlockingEnabled = nextValue;
        RefreshTileValues();
    }

    private void RequestTileAction(MasterControlTileAction action)
    {
        TileActionRequested?.Invoke(this, action);
    }

    private static string CompactUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return value;
        }

        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
    }

    /// <summary>Resolves transparent proxy status after mode application.</summary>
    /// <param name="isTransparentProxyRunning">True when the takeover result reports TUN as running.</param>
    /// <returns>User-facing transparent proxy status text.</returns>
    private string ResolveTransparentProxyStatus(bool isTransparentProxyRunning)
    {
        if (isTransparentProxyRunning)
        {
            return _localization.GetString("Master.Status.Running");
        }

        return _settings.TransparentProxyEnabled
            ? _localization.GetString("Master.Status.Fallback")
            : _localization.GetString("Master.Status.Off");
    }

    private sealed record MasterTileDefinition(
        string Id,
        string Title,
        string Glyph,
        string Description,
        string TypeText,
        bool IsToggleVisible = false,
        bool IsToggleOn = false,
        RelayCommand? Command = null);

    private static class MasterTileCatalog
    {
        public static IReadOnlyList<MasterTileDefinition> Create(MasterControlViewModel owner)
        {
            string infoType = owner._localization.GetString("Master.Tile.Type.Information");
            string controllableType = owner._localization.GetString("Master.Tile.Type.Controllable");
            string actionType = owner._localization.GetString("Master.Tile.Type.Action");
            string navigationType = owner._localization.GetString("Master.Tile.Type.Navigation");

            return
            [
                owner.CreateTile("core", "Core", "\uE950", infoType),
                owner.CreateTile("mihomo-version", "MihomoVersion", "\uE950", infoType),
                owner.CreateTile("system-proxy", "SystemProxy", "\uE968", infoType),
                owner.CreateTile("transparent-proxy", "TransparentProxy", "\uE8A7", controllableType, true, owner._settings.TransparentProxyEnabled, owner.ToggleTransparentProxy),
                owner.CreateTile("latency", "Latency", "\uEC4A", actionType, command: () => owner.RequestTileAction(MasterControlTileAction.RunLatencyTest)),
                owner.CreateTile("startup-launch", "StartupLaunch", "\uE7C3", controllableType, true, owner._settings.LaunchAtStartupEnabled, owner.ToggleStartupLaunch),
                owner.CreateTile("connection-sampling", "ConnectionSampling", "\uE81C", controllableType, true, owner._settings.ConnectionSamplingEnabled, owner.ToggleConnectionSampling),
                owner.CreateTile("blocked-url", "BlockedUrl", "\uE8A7", controllableType, true, owner._settings.MainlandChinaUrlBlockingEnabled, owner.ToggleUrlBlocking),
                owner.CreateTile("active-profile", "ActiveProfile", "\uE8A5", infoType),
                owner.CreateTile("port", "Port", "\uE839", infoType),
                owner.CreateTile("connection-test", "ConnectionTest", "\uE9D9", navigationType),
                owner.CreateTile("connection-test-proxy-url-1", "ConnectionTestProxyUrl1", "\uE774", infoType),
                owner.CreateTile("connection-test-proxy-url-2", "ConnectionTestProxyUrl2", "\uE774", infoType),
                owner.CreateTile("connection-test-direct-url", "ConnectionTestDirectUrl", "\uE8A7", infoType),
                owner.CreateTile("startup-prompt", "StartupPrompt", "\uE946", actionType, command: () => owner.RequestTileAction(MasterControlTileAction.ShowStartupPrompt)),
                owner.CreateTile("startup-conflicts", "StartupConflicts", "\uE9D9", actionType, command: () => owner.RequestTileAction(MasterControlTileAction.CheckStartupConflicts)),
                owner.CreateTile("export-config", "ExportConfig", "\uE74E", actionType, command: () => owner.RequestTileAction(MasterControlTileAction.ExportConfiguration)),
                owner.CreateTile("import-config", "ImportConfig", "\uE8B5", actionType, command: () => owner.RequestTileAction(MasterControlTileAction.ImportConfiguration)),
            ];
        }
    }

    private MasterTileDefinition CreateTile(
        string id,
        string key,
        string glyph,
        string typeText,
        bool isToggleVisible = false,
        bool isToggleOn = false,
        Action? command = null)
    {
        return new MasterTileDefinition(
            id,
            _localization.GetString($"Master.Tile.{key}"),
            glyph,
            TileDescription(key),
            typeText,
            isToggleVisible,
            isToggleOn,
            command is null ? null : new RelayCommand(command));
    }

    private sealed class NoMasterControlApplicationActionDispatcher : IApplicationActionDispatcher
    {
        public static NoMasterControlApplicationActionDispatcher Instance { get; } = new();

        public Task DispatchAsync(ApplicationActionKind kind, string value, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
