using System;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts persistent application settings to the settings view-model storage contract.</summary>
internal sealed class AppSettingsStore : ISettingsStore
{
    private readonly AppSettingsService _settings;

    /// <summary>Initializes an adapter over the supplied persistent settings service.</summary>
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
