/*
 * Settings ViewModel
 * Owns settings state transitions for the settings page without depending on WinUI controls
 *
 * @author: WaterRun
 * @file: ViewModel/SettingsViewModel.cs
 * @date: 2026-06-17
 */

using System;
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

    bool MainlandChinaDisplayEnabled { get; set; }
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

    public bool MainlandChinaDisplayEnabled
    {
        get => _settings.MainlandChinaDisplayEnabled;
        set => _settings.MainlandChinaDisplayEnabled = value;
    }
}

/// <summary>Owns user-editable settings state and persistence for the settings page.</summary>
/// <remarks>
/// Invariants: Numeric values exposed by properties are always within the same valid range enforced by <see cref="AppSettingsService"/>.
/// Thread safety: Not thread-safe; intended for UI-thread use.
/// Side effects: Set methods persist values and may trigger injected application callbacks.
/// </remarks>
internal sealed class SettingsViewModel
{
    /// <summary>Persistent settings store used by this view model.</summary>
    private readonly ISettingsStore _settings;

    /// <summary>Callback invoked when the display language changes.</summary>
    private readonly Action<AppLanguage> _applyLanguage;

    /// <summary>Callback invoked when background connection sampling settings change.</summary>
    private readonly Action _restartConnectionSampling;

    /// <summary>Initializes a new settings view model.</summary>
    /// <param name="settings">Settings store. Must not be null.</param>
    /// <param name="applyLanguage">Callback used to update the active UI language. Must not be null.</param>
    /// <param name="restartConnectionSampling">Callback used to restart connection sampling. Must not be null.</param>
    public SettingsViewModel(
        ISettingsStore settings,
        Action<AppLanguage> applyLanguage,
        Action restartConnectionSampling)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _applyLanguage = applyLanguage ?? throw new ArgumentNullException(nameof(applyLanguage));
        _restartConnectionSampling = restartConnectionSampling ?? throw new ArgumentNullException(nameof(restartConnectionSampling));
        Load();
    }

    public AppLanguage DisplayLanguage { get; private set; }

    public int DisplayLanguageIndex => (int)DisplayLanguage;

    public bool TransparentProxyEnabled { get; private set; }

    public bool FallbackToSystemProxyWhenTunFails { get; private set; }

    public int MixedPort { get; private set; }

    public bool ConnectionSamplingEnabled { get; private set; }

    public int ConnectionSamplingIntervalSeconds { get; private set; }

    public bool CheckStaleProxyOnStartup { get; private set; }

    public bool RestoreProxyOnExit { get; private set; }

    public ProxyRecoveryMode ProxyRecoveryMode { get; private set; }

    public int ProxyRecoveryModeIndex => (int)ProxyRecoveryMode;

    public bool MainlandChinaDisplayEnabled { get; private set; }

    /// <summary>Loads the latest persisted settings into the view model properties.</summary>
    public void Load()
    {
        DisplayLanguage = _settings.DisplayLanguage;
        TransparentProxyEnabled = _settings.TransparentProxyEnabled;
        FallbackToSystemProxyWhenTunFails = _settings.FallbackToSystemProxyWhenTunFails;
        MixedPort = _settings.MixedPort;
        ConnectionSamplingEnabled = _settings.ConnectionSamplingEnabled;
        ConnectionSamplingIntervalSeconds = _settings.ConnectionSamplingIntervalSeconds;
        CheckStaleProxyOnStartup = _settings.CheckStaleProxyOnStartup;
        RestoreProxyOnExit = _settings.RestoreProxyOnExit;
        ProxyRecoveryMode = _settings.ProxyRecoveryMode;
        MainlandChinaDisplayEnabled = _settings.MainlandChinaDisplayEnabled;
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
        return true;
    }

    /// <summary>Persists the transparent proxy switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetTransparentProxyEnabled(bool isEnabled)
    {
        _settings.TransparentProxyEnabled = isEnabled;
        TransparentProxyEnabled = isEnabled;
    }

    /// <summary>Persists the TUN fallback switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetFallbackToSystemProxyWhenTunFails(bool isEnabled)
    {
        _settings.FallbackToSystemProxyWhenTunFails = isEnabled;
        FallbackToSystemProxyWhenTunFails = isEnabled;
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
        return true;
    }

    /// <summary>Persists the background sampling switch and restarts sampling.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetConnectionSamplingEnabled(bool isEnabled)
    {
        _settings.ConnectionSamplingEnabled = isEnabled;
        ConnectionSamplingEnabled = isEnabled;
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
        CheckStaleProxyOnStartup = isEnabled;
    }

    /// <summary>Persists the shutdown proxy restoration switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetRestoreProxyOnExit(bool isEnabled)
    {
        _settings.RestoreProxyOnExit = isEnabled;
        RestoreProxyOnExit = isEnabled;
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

    /// <summary>Persists the mainland China display switch.</summary>
    /// <param name="isEnabled">Switch value.</param>
    public void SetMainlandChinaDisplayEnabled(bool isEnabled)
    {
        _settings.MainlandChinaDisplayEnabled = isEnabled;
        MainlandChinaDisplayEnabled = isEnabled;
    }
}
