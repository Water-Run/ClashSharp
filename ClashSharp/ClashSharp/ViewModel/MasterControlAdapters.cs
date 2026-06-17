/*
 * Master Control Adapters
 * Connects master control view model contracts to application singleton services
 *
 * @author: WaterRun
 * @file: ViewModel/MasterControlAdapters.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Adapts <see cref="LocalizationService"/> to master-control localization.</summary>
/// <remarks>
/// Invariants: Wraps a non-null localization service for the adapter lifetime.
/// Thread safety: Matches the wrapped service and is intended for UI-thread use.
/// Side effects: Reads localized resources from the wrapped service.
/// </remarks>
internal sealed class MasterControlLocalizationAdapter : IMasterControlLocalization
{
    /// <summary>Wrapped localization service.</summary>
    private readonly LocalizationService _localization;

    /// <summary>Initializes a master-control localization adapter.</summary>
    /// <param name="localization">Localization service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localization"/> is null.</exception>
    public MasterControlLocalizationAdapter(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Resolved localized string or fallback key text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public string GetString(string key)
    {
        return _localization.GetString(key);
    }
}

/// <summary>Adapts <see cref="MihomoCoreService"/> to master-control core probing.</summary>
/// <remarks>
/// Invariants: Wraps a non-null core service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Version probing may start a short-lived mihomo process.
/// </remarks>
internal sealed class MasterControlCoreAdapter : IMasterControlCore
{
    /// <summary>Wrapped core service.</summary>
    private readonly MihomoCoreService _core;

    /// <summary>Initializes a master-control core adapter.</summary>
    /// <param name="core">Core service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is null.</exception>
    public MasterControlCoreAdapter(MihomoCoreService core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    /// <summary>Gets the bundled core version text.</summary>
    /// <param name="cancellationToken">Cancels the version probe when requested.</param>
    /// <returns>The first user-facing version line returned by the core.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the wrapped service.
    /// Completion semantics: Does not mutate long-running core state.
    /// </remarks>
    public Task<string> GetVersionTextAsync(CancellationToken cancellationToken)
    {
        return _core.GetVersionTextAsync(cancellationToken);
    }
}

/// <summary>Adapts <see cref="WindowsProxyService"/> to master-control proxy state reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null Windows proxy service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads Windows proxy registry state.
/// </remarks>
internal sealed class MasterControlWindowsProxyAdapter : IMasterControlWindowsProxy
{
    /// <summary>Wrapped Windows proxy service.</summary>
    private readonly WindowsProxyService _windowsProxy;

    /// <summary>Initializes a master-control Windows proxy adapter.</summary>
    /// <param name="windowsProxy">Windows proxy service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="windowsProxy"/> is null.</exception>
    public MasterControlWindowsProxyAdapter(WindowsProxyService windowsProxy)
    {
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
    }

    /// <summary>Gets current Windows system proxy state.</summary>
    /// <returns>Current Windows proxy state.</returns>
    public WindowsProxyState GetCurrentState()
    {
        return _windowsProxy.GetCurrentState();
    }
}

/// <summary>Adapts <see cref="AppSettingsService"/> to master-control settings.</summary>
/// <remarks>
/// Invariants: Wraps a non-null settings service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Setters persist values through the wrapped service.
/// </remarks>
internal sealed class MasterControlSettingsAdapter : IMasterControlSettings
{
    /// <summary>Wrapped settings service.</summary>
    private readonly AppSettingsService _settings;

    /// <summary>Initializes a master-control settings adapter.</summary>
    /// <param name="settings">Settings service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public MasterControlSettingsAdapter(AppSettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Gets or sets the current master takeover mode.</summary>
    /// <value>Current persisted mode.</value>
    public ClashSharpMode CurrentMode
    {
        get => _settings.CurrentMode;
        set => _settings.CurrentMode = value;
    }

    /// <summary>Gets whether transparent proxy is enabled in settings.</summary>
    /// <value>True when transparent proxy is enabled; otherwise false.</value>
    public bool TransparentProxyEnabled => _settings.TransparentProxyEnabled;
}

/// <summary>Adapts <see cref="NetworkTakeoverService"/> to master-control mode application.</summary>
/// <remarks>
/// Invariants: Wraps a non-null takeover service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Applies runtime mode through the wrapped service.
/// </remarks>
internal sealed class MasterControlTakeoverAdapter : IMasterControlTakeover
{
    /// <summary>Wrapped takeover service.</summary>
    private readonly NetworkTakeoverService _takeover;

    /// <summary>Initializes a master-control takeover adapter.</summary>
    /// <param name="takeover">Takeover service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="takeover"/> is null.</exception>
    public MasterControlTakeoverAdapter(NetworkTakeoverService takeover)
    {
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
    }

    /// <summary>Applies a master takeover mode.</summary>
    /// <param name="mode">Mode to apply.</param>
    /// <returns>Result describing the applied runtime state.</returns>
    public NetworkTakeoverResult ApplyMode(ClashSharpMode mode)
    {
        return _takeover.ApplyMode(mode);
    }
}

/// <summary>Adapts <see cref="LogStorageService"/> to master-control logging.</summary>
/// <remarks>
/// Invariants: Wraps a non-null log service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Writes log entries to persistent storage.
/// </remarks>
internal sealed class MasterControlLogAdapter : IMasterControlLog
{
    /// <summary>Wrapped log service.</summary>
    private readonly LogStorageService _log;

    /// <summary>Initializes a master-control log adapter.</summary>
    /// <param name="log">Log service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.</exception>
    public MasterControlLogAdapter(LogStorageService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Appends one log entry.</summary>
    /// <param name="level">Log level. Must not be null.</param>
    /// <param name="category">Log category. Must not be null.</param>
    /// <param name="message">Log summary. Must not be null.</param>
    /// <param name="detail">Optional detail text; null when no detail exists.</param>
    public void Append(string level, string category, string message, string? detail)
    {
        _log.AppendLog(level, category, message, detail);
    }
}
