using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

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

    /// <summary>Durable network action used to apply selected modes.</summary>
    private readonly IMasterControlTakeover _takeover;

    /// <summary>Log sink used by mode application.</summary>
    private readonly IMasterControlLog _log;

    /// <summary>Tray status provider used for current node and latency details.</summary>
    private readonly IMasterControlTrayStatus _trayStatus;

    /// <summary>Runtime summary provider used for count and storage tiles.</summary>
    private readonly IMasterControlRuntime _runtime;

    /// <summary>Shared application action dispatcher used by functional tiles.</summary>
    private readonly IMasterControlActions _actions;

    private readonly AsyncRelayCommand _toggleTransparentProxyCommand;

    private readonly AsyncRelayCommand _toggleStartupLaunchCommand;

    private readonly AsyncRelayCommand _toggleConnectionSamplingCommand;

    private readonly IMasterHeroStatusLayoutService _heroStatusLayout;

    private readonly IMasterInfoTileLayoutService _infoTileLayout;

    private readonly Func<DateTimeOffset> _getNow;

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

    private string _operationErrorText = string.Empty;

    /// <summary>Backing field for <see cref="CurrentNodeText"/>.</summary>
    private string _currentNodeText = string.Empty;

    /// <summary>Backing field for <see cref="LatencySummaryText"/>.</summary>
    private string _latencySummaryText = string.Empty;

    /// <summary>Backing field for the latest formatted mihomo version tile value.</summary>
    private string _mihomoVersionText = string.Empty;

    private const string ApplicationDisplayName = "Clash#";

    private static readonly string ApplicationVersionText = ResolveApplicationVersionText();

    /// <summary>Whether the bundled core was available during the latest status refresh.</summary>
    private bool _isCoreAvailable = true;

    /// <summary>Latest runtime snapshot backing count and storage tiles.</summary>
    private MasterControlRuntimeSnapshot _runtimeSnapshot = MasterControlRuntimeSnapshot.Unavailable;

    /// <summary>Information tiles displayed in the lower grid.</summary>
    private readonly ObservableCollection<MasterControlInfoTileViewModel> _infoTiles = [];

    /// <summary>Currently visible information tiles displayed in the lower grid.</summary>
    private readonly ObservableCollection<MasterControlInfoTileViewModel> _visibleInfoTiles = [];

    private bool _isApplyingInfoTileLayout;

    private readonly ObservableCollection<MasterHeroStatusItemViewModel> _heroStatusItems = [];

    private readonly ObservableCollection<MasterHeroStatusSlotViewModel> _heroStatusSlots = [];

    private IReadOnlyList<MasterHeroStatusOptionViewModel> _heroStatusOptions = [];

    /// <summary>Whether persisted layout and settings state have been loaded for this page lifetime.</summary>
    private bool _isInitialized;

    private DateTimeOffset? _lastHeavyRefreshAt;

    private static readonly TimeSpan LoadRefreshThrottle = TimeSpan.FromSeconds(5);

    /// <summary>Initializes a master control view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="core">Core runtime provider. Must not be null.</param>
    /// <param name="windowsProxy">Windows proxy provider. Must not be null.</param>
    /// <param name="settings">Settings store. Must not be null.</param>
    /// <param name="takeover">Network takeover provider. Must not be null.</param>
    /// <param name="log">Log sink. Must not be null.</param>
    /// <param name="infoTileLayout">Persistent information-tile layout service. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public MasterControlViewModel(
        IMasterControlLocalization localization,
        IMasterControlCore core,
        IMasterControlWindowsProxy windowsProxy,
        IMasterControlSettings settings,
        IMasterControlTakeover takeover,
        IMasterControlLog log,
        IMasterInfoTileLayoutService infoTileLayout,
        IMasterHeroStatusLayoutService heroStatusLayout,
        IApplicationErrorSink errorSink,
        IMasterControlTrayStatus? trayStatus = null,
        IMasterControlRuntime? runtime = null,
        IMasterControlActions? actions = null,
        Func<ClashSharpMode, Task>? modeApplied = null,
        Func<DateTimeOffset>? getNow = null)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _infoTileLayout = infoTileLayout ?? throw new ArgumentNullException(nameof(infoTileLayout));
        _heroStatusLayout = heroStatusLayout ?? throw new ArgumentNullException(nameof(heroStatusLayout));
        ArgumentNullException.ThrowIfNull(errorSink);
        _trayStatus = trayStatus ?? UnavailableMasterControlTrayStatus.Instance;
        _runtime = runtime ?? UnavailableMasterControlRuntime.Instance;
        _actions = actions ?? NoMasterControlApplicationActionDispatcher.Instance;
        _getNow = getNow ?? (() => DateTimeOffset.Now);
        _modeApplied = modeApplied ?? (_ => Task.CompletedTask);
        DisabledModeCommand = new AsyncRelayCommand(
            token => ApplyModeAsync(ClashSharpMode.Disabled, token),
            errorSink,
            operationName: "master-mode-disabled");
        StandbyModeCommand = new AsyncRelayCommand(
            token => ApplyModeAsync(ClashSharpMode.Standby, token),
            errorSink,
            operationName: "master-mode-standby");
        RuleTakeoverModeCommand = new AsyncRelayCommand(
            token => ApplyModeAsync(ClashSharpMode.RuleTakeover, token),
            errorSink,
            operationName: "master-mode-rule-takeover");
        FullTakeoverModeCommand = new AsyncRelayCommand(
            token => ApplyModeAsync(ClashSharpMode.FullTakeover, token),
            errorSink,
            operationName: "master-mode-full-takeover");
        LoadCommand = new AsyncRelayCommand(
            LoadAsync,
            errorSink,
            operationName: "master-load");
        _toggleTransparentProxyCommand = new AsyncRelayCommand(
            ToggleTransparentProxyAsync,
            errorSink,
            operationName: "master-transparent-proxy-setting");
        _toggleStartupLaunchCommand = new AsyncRelayCommand(
            ToggleStartupLaunchAsync,
            errorSink,
            operationName: "master-startup-launch-setting");
        _toggleConnectionSamplingCommand = new AsyncRelayCommand(
            ToggleConnectionSamplingAsync,
            errorSink,
            operationName: "master-connection-sampling-setting");

        CoreStatusText = string.Empty;
        SystemProxyStatusText = string.Empty;
        TransparentProxyStatusText = string.Empty;
        CurrentNodeText = _localization.GetString("Master.Status.CurrentNodeUnavailable");
        LatencySummaryText = _localization.GetString("Master.Status.LatencyUnavailable");
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

    /// <summary>Gets the localized message shown when an on-demand latency test fails.</summary>
    public string LatencyTestFailedText => _localization.GetString("Master.LatencyDialog.Failed");

    public string EditInfoTilesText => _localization.GetString("Master.Tile.Edit");

    public string SearchInfoTilesPlaceholderText => _localization.GetString("Master.Tile.SearchPlaceholder");

    public string InfoTileSelectionDescriptionText =>
        _localization.GetString("Master.Tile.SelectionDescription");

    public string VisibleTileText => _localization.GetString("Master.Tile.Visible");

    public IReadOnlyList<MasterControlInfoTileViewModel> InfoTiles => _infoTiles;

    public IReadOnlyList<MasterControlInfoTileViewModel> VisibleInfoTiles => _visibleInfoTiles;

    public IReadOnlyList<MasterHeroStatusItemViewModel> HeroStatusItems => _heroStatusItems;

    public IReadOnlyList<MasterHeroStatusSlotViewModel> HeroStatusSlots => _heroStatusSlots;

    public IReadOnlyList<MasterHeroStatusOptionViewModel> HeroStatusOptions => _heroStatusOptions;

    public string SetHeroStatusDisplayText => _localization.GetString("Master.Hero.SetDisplay");

    public string RestoreDefaultHeroStatusLayoutText => _localization.GetString("Master.Hero.RestoreDefault");

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
    /// <param name="cancellationToken">Cancels all runtime status probes when requested.</param>
    /// <returns>A task that completes after status text is refreshed.</returns>
    /// <remarks>
    /// Cancellation semantics: Cancellation propagates from every probe and prevents stale results from being committed.
    /// Thread / reentrancy: Not guarded; callers should use <see cref="LoadCommand"/> for UI invocation.
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        DateTimeOffset now = _getNow();
        if (_lastHeavyRefreshAt is DateTimeOffset lastRefresh && now - lastRefresh < LoadRefreshThrottle)
        {
            RefreshTileValues();
            return;
        }

        RefreshProxyStatus();
        await Task.WhenAll(
            RefreshCoreStatusAsync(cancellationToken),
            RefreshRuntimeSnapshotAsync(cancellationToken),
            RefreshTrayStatusAsync(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        OnPropertyChanged(nameof(BasicStatusText));
        RefreshTileValues();
        _lastHeavyRefreshAt = now;
    }

    /// <summary>Loads persisted page state once without recreating collections on repeated Loaded events.</summary>
    private void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        _selectedMode = _settings.CurrentMode;
        _heroStatusOptions = BuildHeroStatusOptions();
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(IsDisabledModeSelected));
        OnPropertyChanged(nameof(IsStandbyModeSelected));
        OnPropertyChanged(nameof(IsRuleTakeoverModeSelected));
        OnPropertyChanged(nameof(IsFullTakeoverModeSelected));
        OnPropertyChanged(nameof(HeroStatusOptions));
        BuildHeroStatusItems();
        BuildInfoTiles();
        _isInitialized = true;
        RefreshTileValues();
    }

    private async Task RefreshCoreStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            string versionText = CoreVersionDisplayFormatter.Format(
                await _core.GetVersionTextAsync(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            _mihomoVersionText = versionText;
            CoreStatusText = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetString("Master.Status.CoreReady.Format"),
                versionText);
            _isCoreAvailable = true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or InvalidOperationException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _mihomoVersionText = _localization.GetString("Master.Status.Unavailable");
            CoreStatusText = _localization.GetString("Master.Status.CoreUnavailable");
            _isCoreAvailable = false;
        }
    }

    /// <summary>Applies a selected takeover mode and refreshes visible status.</summary>
    /// <param name="mode">Mode to apply.</param>
    /// <param name="cancellationToken">Cancels admission or pre-side-effect work.</param>
    /// <returns>A task that completes after verified success or baseline restoration.</returns>
    /// <remarks>
    /// Cancellation semantics: Cancellation is propagated to the durable network coordinator.
    /// Thread / reentrancy: Not guarded; callers should use mode commands for UI invocation.
    /// </remarks>
    public async Task ApplyModeAsync(ClashSharpMode mode, CancellationToken cancellationToken)
    {
        if (mode == SelectedMode && mode == _settings.CurrentMode)
        {
            return;
        }

        ClashSharpMode baselineMode = SelectedMode;
        string baselineCoreStatus = CoreStatusText;
        string baselineSystemProxyStatus = SystemProxyStatusText;
        string baselineTransparentProxyStatus = TransparentProxyStatusText;
        bool baselineCoreAvailable = _isCoreAvailable;
        OperationErrorText = string.Empty;
        try
        {
            NetworkTakeoverResult result = await _takeover
                .ApplyModeAsync(mode, cancellationToken);
            SelectedMode = result.Mode;
            CoreStatusText = result.CoreRunning
                ? _localization.GetString("Master.Status.Running")
                : _localization.GetString("Master.Status.NotRunning");
            SystemProxyStatusText = result.SystemProxyEnabled
                ? _localization.GetString("Master.Status.On")
                : _localization.GetString("Master.Status.Off");
            TransparentProxyStatusText = ResolveTransparentProxyStatus(
                result.TunEffective,
                result.TunRequested);
            _log.Append("Info", "MasterControl", result.Message, null);
            await _modeApplied(result.Mode);
            _isCoreAvailable = true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or InvalidOperationException or Win32Exception or UnauthorizedAccessException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            RestoreModePresentation(
                baselineMode,
                baselineCoreStatus,
                baselineSystemProxyStatus,
                baselineTransparentProxyStatus,
                baselineCoreAvailable);
            string fallbackMessage = _localization.GetString("Master.Log.ApplyModeFailed");
            OperationErrorText = RuntimeFailureDiagnostics.TryExtractCode(
                exception,
                out string? diagnosticCode)
                ? RuntimeFailureDiagnostics.Format(
                    diagnosticCode,
                    _localization.GetString,
                    fallbackMessage)
                : fallbackMessage;
            _log.Append("Error", "MasterControl", fallbackMessage, exception.Message);
        }
        catch (OperationCanceledException)
        {
            RestoreModePresentation(
                baselineMode,
                baselineCoreStatus,
                baselineSystemProxyStatus,
                baselineTransparentProxyStatus,
                baselineCoreAvailable);
            throw;
        }
        catch
        {
            RestoreModePresentation(
                baselineMode,
                baselineCoreStatus,
                baselineSystemProxyStatus,
                baselineTransparentProxyStatus,
                baselineCoreAvailable);
            OperationErrorText = _localization.GetString("Application.UnexpectedError");
            throw;
        }
        finally
        {
            OnPropertyChanged(nameof(BasicStatusText));
            RefreshTileValues();
        }
    }

    private void RestoreModePresentation(
        ClashSharpMode mode,
        string coreStatus,
        string systemProxyStatus,
        string transparentProxyStatus,
        bool isCoreAvailable)
    {
        SelectedMode = mode;
        CoreStatusText = coreStatus;
        SystemProxyStatusText = systemProxyStatus;
        TransparentProxyStatusText = transparentProxyStatus;
        _isCoreAvailable = isCoreAvailable;
    }

    public void SetHeroStatusSlot(int slotIndex, MasterHeroStatusItemKind kind)
    {
        if (slotIndex < 0 || slotIndex >= _heroStatusItems.Count)
        {
            return;
        }

        MasterHeroStatusItemKind[] layout = _heroStatusItems.Select(static item => item.Kind).ToArray();
        int existingIndex = Array.IndexOf(layout, kind);
        if (existingIndex >= 0 && existingIndex != slotIndex)
        {
            layout[existingIndex] = layout[slotIndex];
        }

        layout[slotIndex] = kind;
        IReadOnlyList<MasterHeroStatusItemKind> normalized = _heroStatusLayout.SaveLayout(layout);
        ApplyHeroStatusLayout(normalized);
    }

    public void ResetHeroStatusLayout()
    {
        ApplyHeroStatusLayout(_heroStatusLayout.ResetLayout());
    }

    /// <summary>Applies and persists the ordered set of information tiles shown on the master page.</summary>
    /// <param name="tileIds">Ordered tile identifiers selected by the user.</param>
    public void SetVisibleInfoTileIds(IEnumerable<string> tileIds)
    {
        ArgumentNullException.ThrowIfNull(tileIds);

        string[] availableTileIds = _infoTiles.Select(static tile => tile.Id).ToArray();
        IReadOnlyList<string> layout = _infoTileLayout.SaveLayout(tileIds, availableTileIds);
        ApplyInfoTileLayout(layout);
    }

    /// <summary>Persists the current visible tile order after a drag-and-drop reorder.</summary>
    public void PersistInfoTileOrder()
    {
        string[] availableTileIds = _infoTiles.Select(static tile => tile.Id).ToArray();
        _infoTileLayout.SaveLayout(
            _visibleInfoTiles.Select(static tile => tile.Id),
            availableTileIds);
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
        catch (Exception exception) when (
            exception is InvalidOperationException or UnauthorizedAccessException
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            SystemProxyStatusText = _localization.GetString("Master.Status.Unavailable");
        }

        TransparentProxyStatusText = ResolveTransparentProxyStatus(
            isTransparentProxyRunning: false,
            tunRequested: false);
    }

    private async Task RefreshTrayStatusAsync(CancellationToken cancellationToken)
    {
        TrayStatusSnapshot snapshot = await _trayStatus.GetSnapshotAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CurrentNodeText = string.IsNullOrWhiteSpace(snapshot.CurrentNodeName)
            ? _localization.GetString("Master.Status.CurrentNodeUnavailable")
            : snapshot.CurrentNodeName;
        LatencySummaryText = snapshot.LatencyMilliseconds is int latency
            ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Master.Status.Latency.Format"), latency)
            : _localization.GetString("Master.Status.LatencyUnavailable");
    }

    private async Task RefreshRuntimeSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            MasterControlRuntimeSnapshot snapshot =
                await _runtime.GetSnapshotAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _runtimeSnapshot = snapshot;
            TransparentProxyStatusText = snapshot.RuntimeOwnershipKnown
                ? ResolveTransparentProxyStatus(snapshot.TunEffective, snapshot.TunRequested)
                : _localization.GetString("Master.Status.Unavailable");
        }
        catch (Exception exception) when (
            exception is MasterControlRuntimeUnavailableException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _runtimeSnapshot = MasterControlRuntimeSnapshot.Unavailable;
            TransparentProxyStatusText = _localization.GetString("Master.Status.Unavailable");
        }
    }

    private void BuildInfoTiles()
    {
        _infoTiles.Clear();
        _visibleInfoTiles.Clear();
        IReadOnlyList<MasterTileDefinition> definitions = MasterTileCatalog.Create(this);
        foreach (MasterTileDefinition tile in definitions)
        {
            MasterControlInfoTileViewModel viewModel = new(
                tile.Id,
                tile.Title,
                string.Empty,
                string.Empty,
                tile.Glyph,
                tile.Description,
                tile.TypeText,
                tile.IsToggleVisible,
                tile.IsToggleOn,
                tile.Command);
            viewModel.PropertyChanged += OnInfoTilePropertyChanged;
            _infoTiles.Add(viewModel);
        }

        string[] availableTileIds = definitions.Select(static tile => tile.Id).ToArray();
        ApplyInfoTileLayout(_infoTileLayout.GetLayout(availableTileIds));
    }

    private void ApplyInfoTileLayout(IReadOnlyList<string> tileIds)
    {
        Dictionary<string, MasterControlInfoTileViewModel> tilesById = _infoTiles
            .ToDictionary(static tile => tile.Id, StringComparer.Ordinal);
        HashSet<string> visibleIds = tileIds.ToHashSet(StringComparer.Ordinal);

        _isApplyingInfoTileLayout = true;
        try
        {
            foreach (MasterControlInfoTileViewModel tile in _infoTiles)
            {
                tile.IsVisible = visibleIds.Contains(tile.Id);
            }

            _visibleInfoTiles.Clear();
            foreach (string tileId in tileIds)
            {
                if (tilesById.TryGetValue(tileId, out MasterControlInfoTileViewModel? tile))
                {
                    _visibleInfoTiles.Add(tile);
                }
            }
        }
        finally
        {
            _isApplyingInfoTileLayout = false;
        }

        OnPropertyChanged(nameof(VisibleInfoTiles));
    }

    private void BuildHeroStatusItems()
    {
        _heroStatusItems.Clear();
        _heroStatusSlots.Clear();
        ApplyHeroStatusLayout(_heroStatusLayout.GetLayout());
    }

    private void ApplyHeroStatusLayout(IReadOnlyList<MasterHeroStatusItemKind> layout)
    {
        for (int index = 0; index < layout.Count; index++)
        {
            MasterHeroStatusItemKind kind = layout[index];
            string title = GetHeroStatusTitle(kind);
            string value = GetHeroStatusValue(kind);
            if (index < _heroStatusItems.Count)
            {
                _heroStatusItems[index].Kind = kind;
                _heroStatusItems[index].Title = title;
                _heroStatusItems[index].Value = value;
            }
            else
            {
                _heroStatusItems.Add(new MasterHeroStatusItemViewModel(kind, title, value));
            }

            if (index < _heroStatusSlots.Count)
            {
                _heroStatusSlots[index].SelectedKind = kind;
            }
            else
            {
                _heroStatusSlots.Add(new MasterHeroStatusSlotViewModel(index, GetHeroStatusSlotTitle(index), kind, _heroStatusOptions));
            }
        }

        while (_heroStatusItems.Count > layout.Count)
        {
            _heroStatusItems.RemoveAt(_heroStatusItems.Count - 1);
        }

        while (_heroStatusSlots.Count > layout.Count)
        {
            _heroStatusSlots.RemoveAt(_heroStatusSlots.Count - 1);
        }
    }

    private void RefreshTileValues()
    {
        SetTile("core", GetCoreTileStatusText(), string.Empty);
        SetTile("mihomo-version", _mihomoVersionText, string.Empty);
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
        SetTile("app-name", ApplicationDisplayName, _localization.GetString("About.App.Description"));
        SetTile("app-version", string.Format(CultureInfo.CurrentCulture, _localization.GetString("About.Version.Value.Format"), ApplicationVersionText), string.Empty);
        SetTile("app-runtime", _localization.GetString("About.Runtime.Value"), string.Empty);
        SetTile("current-mode", GetModeTitle(SelectedMode), BasicStatusText);
        SetTile("current-node", CurrentNodeText, LatencySummaryText);
        SetTile("notification-enabled", FormatSwitch(_settings.NotificationEnabled), GetNotificationLevelText(_settings.NotificationLevel), _settings.NotificationEnabled);
        SetTile("notification-level", GetNotificationLevelText(_settings.NotificationLevel), FormatSwitch(_settings.NotificationEnabled));
        SetTile("triggers-enabled", FormatSwitch(_settings.TriggersEnabled), string.Empty, _settings.TriggersEnabled);
        SetTile("trigger-notifications", FormatSwitch(_settings.TriggerNotificationsEnabled), string.Empty, _settings.TriggerNotificationsEnabled);
        SetTile("tray-visible-features", FormatTrayVisibleFeatureCount(_settings.TrayVisibleFeatureIds), string.Empty);
        SetTile("tray-monochrome-icon", FormatSwitch(_settings.TrayUseMonochromeInactiveIcon), string.Empty, _settings.TrayUseMonochromeInactiveIcon);
        SetTile("close-behavior", GetCloseBehaviorText(_settings.CloseBehaviorMode), string.Empty);
        SetTile("startup-behavior", GetStartupBehaviorText(_settings.StartupBehaviorMode), string.Empty);
        SetTile("app-theme", GetAppThemeText(_settings.AppThemeMode), string.Empty);
        SetTile("display-language", GetDisplayLanguageText(_settings.DisplayLanguage), string.Empty);
        SetTile(
            "sampling-interval",
            string.Format(CultureInfo.CurrentCulture, _localization.GetString("Master.Status.Seconds.Format"), _settings.ConnectionSamplingIntervalSeconds),
            FormatSwitch(_settings.ConnectionSamplingEnabled));
        SetTile("app-accent", GetAppAccentColorText(_settings.AppAccentColorMode), _settings.AppAccentColorValue);
        SetTile("restore-proxy-on-exit", FormatSwitch(_settings.RestoreProxyOnExit), string.Empty, _settings.RestoreProxyOnExit);
        SetTile("stale-proxy-check", FormatSwitch(_settings.CheckStaleProxyOnStartup), string.Empty, _settings.CheckStaleProxyOnStartup);
        SetTile("startup-conflict-check", FormatSwitch(_settings.StartupConflictCheckEnabled), string.Empty, _settings.StartupConflictCheckEnabled);
        SetTile("startup-guide", FormatSwitch(_settings.ShowStartupGuideOnStartup), string.Empty, _settings.ShowStartupGuideOnStartup);
        SetTile("mainland-feature-mode", GetMainlandChinaFeatureText(_settings.MainlandChinaFeatureMode), string.Empty);
        SetTile("startup-restore-fallback", GetStartupRestoreFallbackStatusText(), CompactPath(_runtimeSnapshot.StartupRestoreFallback.CommandLine));
        SetTile("mihomo-service", GetMihomoServiceStatusText(), string.Empty);
        SetTile("core-config-file", GetCoreConfigurationStatusText(), CompactPath(_runtimeSnapshot.CoreConfiguration.ConfigPath));
        SetTile("upload-rate", FormatBytesPerSecond(_runtimeSnapshot.RuntimeTraffic.UploadBytesPerSecond), _localization.GetString("Master.Tile.Detail.Realtime"));
        SetTile("download-rate", FormatBytesPerSecond(_runtimeSnapshot.RuntimeTraffic.DownloadBytesPerSecond), _localization.GetString("Master.Tile.Detail.Realtime"));
        SetTile("active-connections", FormatNumber(_runtimeSnapshot.RuntimeTraffic.ActiveConnectionCount), _localization.GetString("Master.Tile.Detail.Realtime"));
        SetTile(
            "session-traffic",
            FormatBytes(_runtimeSnapshot.RuntimeTraffic.SessionUploadBytes + _runtimeSnapshot.RuntimeTraffic.SessionDownloadBytes),
            string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetString("Statistics.TotalTraffic.Format"),
                FormatBytes(_runtimeSnapshot.RuntimeTraffic.SessionUploadBytes),
                FormatBytes(_runtimeSnapshot.RuntimeTraffic.SessionDownloadBytes)));
        SetTile("memory-usage", FormatBytes(_runtimeSnapshot.AppWorkingSetBytes), _localization.GetString("Master.Tile.Detail.AppProcess"));
        SetTile("profile-count", FormatNumber(_runtimeSnapshot.ProfileCount), _settings.ActiveProfileId);
        SetTile("subscription-count", FormatNumber(_runtimeSnapshot.SubscriptionCount), string.Empty);
        SetTile("proxy-node-count", FormatNumber(_runtimeSnapshot.ProxyNodeCount), string.Empty);
        SetTile("rule-count", FormatNumber(_runtimeSnapshot.RuleCount), string.Empty);
        SetTile("trigger-count", FormatEnabledCount(_runtimeSnapshot.EnabledTriggerTaskCount, _runtimeSnapshot.TriggerTaskCount), FormatSwitch(_settings.TriggersEnabled));
        SetTile("system-log-count", FormatNumber(_runtimeSnapshot.LogStorage.LogCount), FormatBytes(_runtimeSnapshot.LogStorage.DatabaseSizeBytes));
        SetTile("connection-records", FormatNumber(_runtimeSnapshot.LogStorage.ConnectionCount), FormatBytes(_runtimeSnapshot.LogStorage.DatabaseSizeBytes));
        SetTile(
            "traffic-total",
            FormatBytes(_runtimeSnapshot.Traffic.TotalUploadBytes + _runtimeSnapshot.Traffic.TotalDownloadBytes),
            string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetString("Statistics.TotalTraffic.Format"),
                FormatBytes(_runtimeSnapshot.Traffic.TotalUploadBytes),
                FormatBytes(_runtimeSnapshot.Traffic.TotalDownloadBytes)));
        SetTile("traffic-snapshots", FormatNumber(_runtimeSnapshot.Traffic.SnapshotCount), string.Empty);
        SetTile("node-health-records", FormatNumber(_runtimeSnapshot.Traffic.NodeHealthCount), FormatNumber(_runtimeSnapshot.Traffic.NodeCount));
        RefreshHeroStatusValues();
    }

    private void RefreshHeroStatusValues()
    {
        foreach (MasterHeroStatusItemViewModel item in _heroStatusItems)
        {
            item.Title = GetHeroStatusTitle(item.Kind);
            item.Value = GetHeroStatusValue(item.Kind);
        }
    }

    private IReadOnlyList<MasterHeroStatusOptionViewModel> BuildHeroStatusOptions()
    {
        return _heroStatusLayout
            .GetCandidates()
            .Select(kind => new MasterHeroStatusOptionViewModel(kind, GetHeroStatusTitle(kind)))
            .ToArray();
    }

    private string GetHeroStatusSlotTitle(int slotIndex)
    {
        return slotIndex switch
        {
            0 => _localization.GetString("Master.Hero.Slot.Row1Left"),
            1 => _localization.GetString("Master.Hero.Slot.Row1Right"),
            2 => _localization.GetString("Master.Hero.Slot.Row2Left"),
            3 => _localization.GetString("Master.Hero.Slot.Row2Right"),
            4 => _localization.GetString("Master.Hero.Slot.Row3Left"),
            5 => _localization.GetString("Master.Hero.Slot.Row3Right"),
            6 => _localization.GetString("Master.Hero.Slot.Row4Left"),
            7 => _localization.GetString("Master.Hero.Slot.Row4Right"),
            _ => string.Empty,
        };
    }

    private string GetHeroStatusTitle(MasterHeroStatusItemKind kind)
    {
        return _localization.GetString($"Master.Hero.Item.{kind}");
    }

    private string GetHeroStatusValue(MasterHeroStatusItemKind kind)
    {
        return kind switch
        {
            MasterHeroStatusItemKind.CoreStatus => GetCoreTileStatusText(),
            MasterHeroStatusItemKind.SystemProxy => SystemProxyStatusText,
            MasterHeroStatusItemKind.TransparentProxy => TransparentProxyStatusText,
            MasterHeroStatusItemKind.CurrentNode => CurrentNodeText,
            MasterHeroStatusItemKind.Latency => LatencySummaryText,
            MasterHeroStatusItemKind.UploadRate => FormatBytesPerSecond(_runtimeSnapshot.RuntimeTraffic.UploadBytesPerSecond),
            MasterHeroStatusItemKind.DownloadRate => FormatBytesPerSecond(_runtimeSnapshot.RuntimeTraffic.DownloadBytesPerSecond),
            MasterHeroStatusItemKind.TotalTraffic => FormatBytes(_runtimeSnapshot.Traffic.TotalUploadBytes + _runtimeSnapshot.Traffic.TotalDownloadBytes),
            MasterHeroStatusItemKind.ActiveConnections => FormatNumber(_runtimeSnapshot.RuntimeTraffic.ActiveConnectionCount),
            MasterHeroStatusItemKind.CurrentMode => GetModeTitle(SelectedMode),
            MasterHeroStatusItemKind.ActiveProfile => _settings.ActiveProfileId,
            MasterHeroStatusItemKind.MihomoService => GetMihomoServiceStatusText(),
            MasterHeroStatusItemKind.StartupLaunch => _settings.LaunchAtStartupEnabled
                ? _localization.GetString("Master.Status.StartupLaunchOn")
                : _localization.GetString("Master.Status.StartupLaunchOff"),
            MasterHeroStatusItemKind.Availability => _isCoreAvailable
                ? _localization.GetString("Master.Status.Available")
                : _localization.GetString("Master.Status.Unavailable"),
            _ => string.Empty,
        };
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

    private void OnInfoTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MasterControlInfoTileViewModel.IsVisible)
            || sender is not MasterControlInfoTileViewModel tile)
        {
            return;
        }

        if (_isApplyingInfoTileLayout)
        {
            return;
        }

        if (tile.IsVisible)
        {
            int sourceIndex = _infoTiles.IndexOf(tile);
            int visibleIndex = _visibleInfoTiles
                .Select(item => _infoTiles.IndexOf(item))
                .TakeWhile(index => index < sourceIndex)
                .Count();
            if (!_visibleInfoTiles.Contains(tile))
            {
                _visibleInfoTiles.Insert(visibleIndex, tile);
            }
        }
        else
        {
            _visibleInfoTiles.Remove(tile);
        }

        OnPropertyChanged(nameof(VisibleInfoTiles));
        PersistInfoTileOrder();
    }

    private async Task ToggleTransparentProxyAsync(CancellationToken cancellationToken)
    {
        bool desired = !_settings.TransparentProxyEnabled;
        OperationErrorText = string.Empty;
        try
        {
            await _actions.DispatchAsync(
                ApplicationActionKind.SetTransparentProxy,
                desired.ToString(),
                cancellationToken);
            await RefreshRuntimeSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await RefreshRuntimeSnapshotAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await RefreshRuntimeSnapshotAsync(CancellationToken.None);
            string fallbackMessage = _localization.GetString("Application.UnexpectedError");
            OperationErrorText = RuntimeFailureDiagnostics.TryExtractCode(
                exception,
                out string? diagnosticCode)
                ? RuntimeFailureDiagnostics.Format(
                    diagnosticCode,
                    _localization.GetString,
                    fallbackMessage)
                : fallbackMessage;
            throw;
        }
        finally
        {
            RefreshTileValues();
        }
    }

    private async Task ToggleStartupLaunchAsync(CancellationToken cancellationToken)
    {
        await ToggleSettingAsync(
            ApplicationActionKind.SetLaunchAtStartup,
            () => _settings.LaunchAtStartupEnabled,
            cancellationToken);
    }

    private async Task ToggleConnectionSamplingAsync(CancellationToken cancellationToken)
    {
        await ToggleSettingAsync(
            ApplicationActionKind.SetConnectionSampling,
            () => _settings.ConnectionSamplingEnabled,
            cancellationToken);
    }

    private async Task ToggleSettingAsync(
        ApplicationActionKind kind,
        Func<bool> getValue,
        CancellationToken cancellationToken)
    {
        bool baseline = getValue();
        bool desired = !baseline;
        OperationErrorText = string.Empty;
        try
        {
            await _actions.DispatchAsync(kind, desired.ToString(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            OperationErrorText = _localization.GetString("Application.UnexpectedError");
            throw;
        }
        finally
        {
            RefreshTileValues();
        }
    }

    private void ToggleUrlBlocking()
    {
        bool nextValue = !_settings.MainlandChinaUrlBlockingEnabled;
        _settings.MainlandChinaUrlBlockingEnabled = nextValue;
        RefreshTileValues();
    }

    private void ToggleRestoreProxyOnExit()
    {
        _settings.RestoreProxyOnExit = !_settings.RestoreProxyOnExit;
        RefreshTileValues();
    }

    private void ToggleCheckStaleProxyOnStartup()
    {
        _settings.CheckStaleProxyOnStartup = !_settings.CheckStaleProxyOnStartup;
        RefreshTileValues();
    }

    private void ToggleStartupConflictCheck()
    {
        _settings.StartupConflictCheckEnabled = !_settings.StartupConflictCheckEnabled;
        RefreshTileValues();
    }

    private void ToggleStartupGuide()
    {
        _settings.ShowStartupGuideOnStartup = !_settings.ShowStartupGuideOnStartup;
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

    private string FormatSwitch(bool isEnabled)
    {
        return isEnabled
            ? _localization.GetString("Master.Status.On")
            : _localization.GetString("Master.Status.Off");
    }

    private string GetCoreTileStatusText()
    {
        return _isCoreAvailable
            ? _localization.GetString("Master.BasicStatus.Ready")
            : _localization.GetString("Master.Status.CoreUnavailable");
    }

    private string GetStartupRestoreFallbackStatusText()
    {
        return _runtimeSnapshot.StartupRestoreFallback.IsRegistered
            ? _localization.GetString("Settings.StartupRestoreFallback.Status.Registered")
            : _localization.GetString("Settings.StartupRestoreFallback.Status.NotRegistered");
    }

    private string GetMihomoServiceStatusText()
    {
        if (!string.IsNullOrWhiteSpace(_runtimeSnapshot.MihomoService.Message))
        {
            return _runtimeSnapshot.MihomoService.Message;
        }

        if (!_runtimeSnapshot.MihomoService.IsKnown)
        {
            return _localization.GetString("MihomoService.Status.Unknown");
        }

        return _runtimeSnapshot.MihomoService.IsRunning
            ? _localization.GetString("MihomoService.Status.DeployedRunning")
            : _runtimeSnapshot.MihomoService.IsInstalled
                ? _localization.GetString("MihomoService.Status.Deployed")
                : _localization.GetString("MihomoService.Status.NotDeployed");
    }

    private string GetCoreConfigurationStatusText()
    {
        return _runtimeSnapshot.CoreConfiguration.Exists
            ? _localization.GetString("ProfileCatalog.Status.Available")
            : _localization.GetString("Settings.ProxyInformation.CoreBinary.Missing");
    }

    private string GetAppAccentColorText(AppAccentColorMode mode)
    {
        return mode switch
        {
            AppAccentColorMode.FollowSystem => _localization.GetString("Settings.AppAccentColor.FollowSystem"),
            AppAccentColorMode.Custom => _localization.GetString("Settings.AppAccentColor.Custom"),
            _ => _localization.GetString("Settings.AppAccentColor.FollowSystem"),
        };
    }

    private string GetMainlandChinaFeatureText(MainlandChinaFeatureMode mode)
    {
        return mode switch
        {
            MainlandChinaFeatureMode.Disabled => _localization.GetString("Settings.MainlandChinaFeature.Disabled"),
            MainlandChinaFeatureMode.FlagReplacementOnly => _localization.GetString("Settings.MainlandChinaFeature.FlagOnly"),
            MainlandChinaFeatureMode.FlagReplacementAndTextCompletion => _localization.GetString("Settings.MainlandChinaFeature.FlagAndText"),
            MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter => _localization.GetString("Settings.MainlandChinaFeature.KeywordFilter"),
            MainlandChinaFeatureMode.AllIncludingUrlBlacklist => _localization.GetString("Settings.MainlandChinaFeature.All"),
            _ => _localization.GetString("Settings.MainlandChinaFeature.Disabled"),
        };
    }

    private static string FormatNumber(long value)
    {
        return Math.Max(0, value).ToString("N0", CultureInfo.CurrentCulture);
    }

    private static string FormatEnabledCount(int enabledCount, int totalCount)
    {
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{Math.Max(0, enabledCount):N0}/{Math.Max(0, totalCount):N0}");
    }

    private static string FormatBytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", value, units[unitIndex]);
    }

    private static string FormatBytesPerSecond(long bytes)
    {
        return string.Format(CultureInfo.CurrentCulture, "{0}/s", FormatBytes(bytes));
    }

    private static string CompactPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string fileName = Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(fileName) ? value : fileName;
    }

    private string FormatTrayVisibleFeatureCount(string value)
    {
        int count = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return string.Format(CultureInfo.CurrentCulture, _localization.GetString("Settings.Tray.VisibleFeatures.Summary.Format"), count);
    }

    private string GetModeTitle(ClashSharpMode mode)
    {
        return mode switch
        {
            ClashSharpMode.Disabled => DisabledModeTitleText,
            ClashSharpMode.Standby => StandbyModeTitleText,
            ClashSharpMode.RuleTakeover => RuleTakeoverModeTitleText,
            ClashSharpMode.FullTakeover => FullTakeoverModeTitleText,
            _ => _localization.GetString("Master.Status.Unavailable"),
        };
    }

    private string GetNotificationLevelText(NotificationLevel level)
    {
        return level switch
        {
            NotificationLevel.Default => _localization.GetString("Settings.Notification.Default"),
            NotificationLevel.CriticalOnly => _localization.GetString("Settings.Notification.CriticalOnly"),
            NotificationLevel.More => _localization.GetString("Settings.Notification.More"),
            _ => _localization.GetString("Settings.Notification.Default"),
        };
    }

    private string GetCloseBehaviorText(CloseBehaviorMode mode)
    {
        return mode switch
        {
            CloseBehaviorMode.ExitWithoutConfirmation => _localization.GetString("Settings.CloseBehavior.ExitWithoutConfirmation"),
            CloseBehaviorMode.ConfirmExit => _localization.GetString("Settings.CloseBehavior.ConfirmExit"),
            CloseBehaviorMode.MinimizeToTray => _localization.GetString("Settings.CloseBehavior.MinimizeToTray"),
            _ => _localization.GetString("Settings.CloseBehavior.MinimizeToTray"),
        };
    }

    private string GetStartupBehaviorText(StartupBehaviorMode mode)
    {
        return mode switch
        {
            StartupBehaviorMode.LastSetting => _localization.GetString("Settings.StartupBehavior.LastSetting"),
            StartupBehaviorMode.StartRuleProxy => _localization.GetString("Settings.StartupBehavior.StartRuleProxy"),
            StartupBehaviorMode.DisableProxy => _localization.GetString("Settings.StartupBehavior.DisableProxy"),
            _ => _localization.GetString("Settings.StartupBehavior.LastSetting"),
        };
    }

    private string GetAppThemeText(AppThemeMode mode)
    {
        return mode switch
        {
            AppThemeMode.FollowSystem => _localization.GetString("Settings.AppTheme.FollowSystem"),
            AppThemeMode.Light => _localization.GetString("Settings.AppTheme.Light"),
            AppThemeMode.Dark => _localization.GetString("Settings.AppTheme.Dark"),
            _ => _localization.GetString("Settings.AppTheme.FollowSystem"),
        };
    }

    private string GetDisplayLanguageText(AppLanguage language)
    {
        return language switch
        {
            AppLanguage.AutoDetect => _localization.GetString("Settings.Language.AutoDetect"),
            AppLanguage.SimplifiedChinese => "简体中文",
            AppLanguage.TraditionalChinese => "繁體中文",
            AppLanguage.English => "English",
            AppLanguage.Russian => "Русский",
            AppLanguage.French => "Français",
            AppLanguage.German => "Deutsch",
            _ => _localization.GetString("Settings.Language.AutoDetect"),
        };
    }

    /// <summary>Resolves transparent proxy status after mode application.</summary>
    /// <param name="isTransparentProxyRunning">True when the takeover result reports TUN as running.</param>
    /// <returns>User-facing transparent proxy status text.</returns>
    private string ResolveTransparentProxyStatus(
        bool isTransparentProxyRunning,
        bool tunRequested)
    {
        if (isTransparentProxyRunning)
        {
            return _localization.GetString("Master.Status.Running");
        }

        return tunRequested
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
        ICommand? Command = null);

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
                owner.CreateTile("upload-rate", "UploadRate", "\uE898", infoType),
                owner.CreateTile("download-rate", "DownloadRate", "\uE896", infoType),
                owner.CreateTile("active-connections", "ActiveConnections", "\uE839", infoType),
                owner.CreateTile("session-traffic", "SessionTraffic", "\uE9D2", infoType),
                owner.CreateTile("memory-usage", "MemoryUsage", "\uE950", infoType),
                owner.CreateTile("mihomo-version", "MihomoVersion", "\uE950", infoType),
                owner.CreateTile("system-proxy", "SystemProxy", "\uE968", infoType),
                owner.CreateTile("transparent-proxy", "TransparentProxy", "\uE8A7", controllableType, true, owner._settings.TransparentProxyEnabled, trackedCommand: owner._toggleTransparentProxyCommand),
                owner.CreateTile("latency", "Latency", "\uEC4A", actionType, command: () => owner.RequestTileAction(MasterControlTileAction.RunLatencyTest)),
                owner.CreateTile("startup-launch", "StartupLaunch", "\uE7C3", controllableType, true, owner._settings.LaunchAtStartupEnabled, trackedCommand: owner._toggleStartupLaunchCommand),
                owner.CreateTile("connection-sampling", "ConnectionSampling", "\uE81C", controllableType, true, owner._settings.ConnectionSamplingEnabled, trackedCommand: owner._toggleConnectionSamplingCommand),
                owner.CreateTile("blocked-url", "BlockedUrl", "\uE8A7", controllableType, true, owner._settings.MainlandChinaUrlBlockingEnabled, command: owner.ToggleUrlBlocking),
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
                owner.CreateTile("app-name", "AppName", "\uE946", infoType),
                owner.CreateTileFromKeys("app-version", "About.Version.Title", "\uE946", "Master.Tile.Description.AppVersion", infoType),
                owner.CreateTileFromKeys("app-runtime", "About.Runtime.Title", "\uE7F8", "Master.Tile.Description.AppRuntime", infoType),
                owner.CreateTileFromKeys("current-mode", "Tray.Menu.Mode", "\uE8AB", "Master.Mode.RuleTakeover.Description", infoType),
                owner.CreateTileFromKeys("current-node", "Tray.Status.Node.Format", "\uE8A5", "Settings.Tray.Feature.Status.Description", infoType),
                owner.CreateTileFromKeys("notification-enabled", "Settings.Notification.Enabled.Title", "\uE7F4", "Settings.Notification.Enabled.Description", infoType),
                owner.CreateTileFromKeys("notification-level", "Settings.Notification.Title", "\uE7F4", "Settings.Notification.Description", infoType),
                owner.CreateTileFromKeys("triggers-enabled", "Settings.Triggers.Enabled.Title", "\uE9F5", "Settings.Triggers.Enabled.Description", infoType),
                owner.CreateTileFromKeys("trigger-notifications", "Settings.Triggers.Notifications.Title", "\uE7F4", "Settings.Triggers.Notifications.Description", infoType),
                owner.CreateTileFromKeys("tray-visible-features", "Settings.Tray.VisibleFeatures.Title", "\uE8A7", "Settings.Tray.VisibleFeatures.Description", infoType),
                owner.CreateTileFromKeys("tray-monochrome-icon", "Settings.Tray.MonochromeInactiveIcon.Title", "\uE790", "Settings.Tray.MonochromeInactiveIcon.Description", infoType),
                owner.CreateTileFromKeys("close-behavior", "Settings.CloseBehavior.Title", "\uE8BB", "Settings.CloseBehavior.Description", infoType),
                owner.CreateTileFromKeys("startup-behavior", "Settings.StartupBehavior.Title", "\uE7C3", "Settings.StartupBehavior.Description", infoType),
                owner.CreateTileFromKeys("app-theme", "Settings.AppTheme.Title", "\uE790", "Settings.AppTheme.Description", infoType),
                owner.CreateTileFromKeys("display-language", "Settings.Language.Title", "\uE774", "Settings.Language.Description", infoType),
                owner.CreateTileFromKeys("sampling-interval", "Settings.SamplingInterval.Title", "\uE916", "Settings.SamplingInterval.Description", infoType),
                owner.CreateTileFromKeys("app-accent", "Settings.AppAccentColor.Title", "\uE790", "Settings.AppAccentColor.Description", infoType),
                owner.CreateTileFromKeys("restore-proxy-on-exit", "Settings.RestoreProxyOnExit.Title", "\uE8BB", "Settings.RestoreProxyOnExit.Description", controllableType, true, owner._settings.RestoreProxyOnExit, command: owner.ToggleRestoreProxyOnExit),
                owner.CreateTileFromKeys("stale-proxy-check", "Settings.CheckStaleProxy.Title", "\uE9D9", "Settings.CheckStaleProxy.Description", controllableType, true, owner._settings.CheckStaleProxyOnStartup, command: owner.ToggleCheckStaleProxyOnStartup),
                owner.CreateTileFromKeys("startup-conflict-check", "Settings.StartupConflictCheck.Title", "\uE9D9", "Settings.StartupConflictCheck.Description", controllableType, true, owner._settings.StartupConflictCheckEnabled, command: owner.ToggleStartupConflictCheck),
                owner.CreateTileFromKeys("startup-guide", "Settings.StartupGuide.Title", "\uE946", "Settings.StartupGuide.Description", controllableType, true, owner._settings.ShowStartupGuideOnStartup, command: owner.ToggleStartupGuide),
                owner.CreateTileFromKeys("mainland-feature-mode", "Settings.MainlandChinaDisplay.Title", "\uE7B5", "Settings.MainlandChinaDisplay.Description", infoType),
                owner.CreateTileFromKeys("startup-restore-fallback", "Settings.StartupRestoreFallback.Title", "\uE7C3", "Settings.StartupRestoreFallback.Description", infoType),
                owner.CreateTileFromKeys("mihomo-service", "Settings.TransparentProxy.Service.Title", "\uE95A", "Settings.TransparentProxy.Service.Description", infoType),
                owner.CreateTileFromKeys("core-config-file", "Master.Status.CoreConfiguration", "\uE8A5", "Settings.ProxyInformation.Description", infoType),
                owner.CreateTileFromKeys("profile-count", "Nav.Profiles", "\uE8A5", "Page.Profiles.Description", infoType),
                owner.CreateTileFromKeys("subscription-count", "StartupPrompt.Check.Subscription.Title", "\uE774", "Page.Profiles.Description", infoType),
                owner.CreateTileFromKeys("proxy-node-count", "Nav.ProxyNodes", "\uE8A5", "Page.ProxyNodes.Description", infoType),
                owner.CreateTileFromKeys("rule-count", "Nav.Rules", "\uE8D7", "Page.Rules.Description", infoType),
                owner.CreateTileFromKeys("trigger-count", "Settings.Section.Triggers", "\uE9F5", "Page.Triggers.Description", infoType),
                owner.CreateTileFromKeys("system-log-count", "Statistics.LogsShortcut.Title", "\uE9D9", "Statistics.LogsShortcut.Description", infoType),
                owner.CreateTileFromKeys("connection-records", "Nav.Connections", "\uE839", "Page.Connections.Description", infoType),
                owner.CreateTileFromKeys("traffic-total", "Statistics.Total.Title", "\uE9D2", "Page.Statistics.Description", infoType),
                owner.CreateTileFromKeys("traffic-snapshots", "Statistics.ByDate.Title", "\uE121", "Page.Statistics.Description", infoType),
                owner.CreateTileFromKeys("node-health-records", "Statistics.Node.Title", "\uE8A5", "Page.Statistics.Description", infoType),
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
        Action? command = null,
        ICommand? trackedCommand = null)
    {
        return new MasterTileDefinition(
            id,
            _localization.GetString($"Master.Tile.{key}"),
            glyph,
            TileDescription(key),
            typeText,
            isToggleVisible,
            isToggleOn,
            trackedCommand ?? (command is null ? null : new RelayCommand(command)));
    }

    private MasterTileDefinition CreateTileFromKeys(
        string id,
        string titleKey,
        string glyph,
        string descriptionKey,
        string typeText,
        bool isToggleVisible = false,
        bool isToggleOn = false,
        Action? command = null)
    {
        return new MasterTileDefinition(
            id,
            CleanTileTitle(_localization.GetString(titleKey)),
            glyph,
            _localization.GetString(descriptionKey),
            typeText,
            isToggleVisible,
            isToggleOn,
            command is null ? null : new RelayCommand(command));
    }

    private static string CleanTileTitle(string title)
    {
        return title.Replace("{0}", string.Empty, StringComparison.Ordinal).Trim().TrimEnd(':', '：');
    }

    private static string ResolveApplicationVersionText()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0.0" : version.ToString();
    }

    private sealed class NoMasterControlApplicationActionDispatcher : IMasterControlActions
    {
        public static NoMasterControlApplicationActionDispatcher Instance { get; } = new();

        public Task DispatchAsync(ApplicationActionKind kind, string value, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
