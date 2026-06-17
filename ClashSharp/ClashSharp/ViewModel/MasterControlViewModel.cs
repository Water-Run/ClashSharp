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
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

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

    /// <summary>Gets whether transparent proxy is enabled in settings.</summary>
    /// <value>True when transparent proxy is enabled; otherwise false.</value>
    bool TransparentProxyEnabled { get; }
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

    /// <summary>Backing field for <see cref="SelectedMode"/>.</summary>
    private ClashSharpMode _selectedMode;

    /// <summary>Backing field for <see cref="CoreStatusText"/>.</summary>
    private string _coreStatusText = string.Empty;

    /// <summary>Backing field for <see cref="SystemProxyStatusText"/>.</summary>
    private string _systemProxyStatusText = string.Empty;

    /// <summary>Backing field for <see cref="TransparentProxyStatusText"/>.</summary>
    private string _transparentProxyStatusText = string.Empty;

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
        IMasterControlLog log)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _selectedMode = _settings.CurrentMode;

        DisabledModeCommand = new AsyncRelayCommand(token => ApplyModeAsync(ClashSharpMode.Disabled, token));
        StandbyModeCommand = new AsyncRelayCommand(token => ApplyModeAsync(ClashSharpMode.Standby, token));
        RuleTakeoverModeCommand = new AsyncRelayCommand(token => ApplyModeAsync(ClashSharpMode.RuleTakeover, token));
        FullTakeoverModeCommand = new AsyncRelayCommand(token => ApplyModeAsync(ClashSharpMode.FullTakeover, token));
        LoadCommand = new AsyncRelayCommand(LoadAsync);

        CoreStatusText = string.Empty;
        SystemProxyStatusText = string.Empty;
        TransparentProxyStatusText = string.Empty;
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
            string versionText = await _core.GetVersionTextAsync(cancellationToken);
            CoreStatusText = string.Format(
                _localization.GetString("Master.Status.CoreReady.Format"),
                versionText);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException)
        {
            CoreStatusText = _localization.GetString("Master.Status.CoreUnavailable");
        }

        RefreshProxyStatus();
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
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            SelectedMode = ClashSharpMode.Faulted;
            CoreStatusText = _localization.GetString("Master.Status.CoreStartFailed");
            _log.Append("Error", "MasterControl", "Failed to apply selected Clash# mode.", exception.Message);
        }

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
}
