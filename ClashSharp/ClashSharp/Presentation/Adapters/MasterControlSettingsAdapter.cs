using System;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

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

    /// <summary>Gets or sets whether transparent proxy is enabled in settings.</summary>
    /// <value>True when transparent proxy is enabled; otherwise false.</value>
    public bool TransparentProxyEnabled
    {
        get => _settings.TransparentProxyEnabled;
        set => _settings.TransparentProxyEnabled = value;
    }

    public bool LaunchAtStartupEnabled
    {
        get => _settings.LaunchAtStartupEnabled;
        set => _settings.LaunchAtStartupEnabled = value;
    }

    public bool ConnectionSamplingEnabled
    {
        get => _settings.ConnectionSamplingEnabled;
        set => _settings.ConnectionSamplingEnabled = value;
    }

    public bool MainlandChinaUrlBlockingEnabled
    {
        get => _settings.MainlandChinaUrlBlockingEnabled;
        set => _settings.MainlandChinaUrlBlockingEnabled = value;
    }

    public string ActiveProfileId => _settings.ActiveProfileId;

    public int MixedPort => _settings.MixedPort;

    public string ConnectionTestProxyUrl1 => _settings.ConnectionTestProxyUrl1;

    public string ConnectionTestProxyUrl2 => _settings.ConnectionTestProxyUrl2;

    public string ConnectionTestDirectUrl => _settings.ConnectionTestDirectUrl;

    public AppLanguage DisplayLanguage => _settings.DisplayLanguage;

    public AppThemeMode AppThemeMode => _settings.AppThemeMode;

    public int ConnectionSamplingIntervalSeconds => _settings.ConnectionSamplingIntervalSeconds;

    public StartupBehaviorMode StartupBehaviorMode => _settings.StartupBehaviorMode;

    public bool TriggersEnabled => _settings.TriggersEnabled;

    public bool TriggerNotificationsEnabled => _settings.TriggerNotificationsEnabled;

    public CloseBehaviorMode CloseBehaviorMode => _settings.CloseBehaviorMode;

    public bool TrayUseMonochromeInactiveIcon => _settings.TrayUseMonochromeInactiveIcon;

    public string TrayVisibleFeatureIds => _settings.TrayVisibleFeatureIds;

    public bool NotificationEnabled => _settings.NotificationEnabled;

    public NotificationLevel NotificationLevel => _settings.NotificationLevel;

    public bool RestoreProxyOnExit
    {
        get => _settings.RestoreProxyOnExit;
        set => _settings.RestoreProxyOnExit = value;
    }

    public bool CheckStaleProxyOnStartup
    {
        get => _settings.CheckStaleProxyOnStartup;
        set => _settings.CheckStaleProxyOnStartup = value;
    }

    public bool StartupConflictCheckEnabled
    {
        get => _settings.StartupConflictCheckEnabled;
        set => _settings.StartupConflictCheckEnabled = value;
    }

    public bool ShowStartupGuideOnStartup
    {
        get => _settings.ShowStartupGuideOnStartup;
        set => _settings.ShowStartupGuideOnStartup = value;
    }

    public MainlandChinaFeatureMode MainlandChinaFeatureMode => _settings.MainlandChinaFeatureMode;

    public AppAccentColorMode AppAccentColorMode => _settings.AppAccentColorMode;

    public string AppAccentColorValue => _settings.AppAccentColorValue;
}
