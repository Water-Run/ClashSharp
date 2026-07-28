using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Settings contract required by <see cref="MasterControlViewModel"/>.</summary>
internal interface IMasterControlSettings
{
    ClashSharpMode CurrentMode { get; set; }
    bool TransparentProxyEnabled { get; set; }
    bool LaunchAtStartupEnabled { get; set; }
    bool ConnectionSamplingEnabled { get; set; }
    bool MainlandChinaUrlBlockingEnabled { get; set; }
    string ActiveProfileId { get; }
    int MixedPort { get; }
    string ConnectionTestProxyUrl1 { get; }
    string ConnectionTestProxyUrl2 { get; }
    string ConnectionTestDirectUrl { get; }
    AppLanguage DisplayLanguage { get; }
    AppThemeMode AppThemeMode { get; }
    int ConnectionSamplingIntervalSeconds { get; }
    StartupBehaviorMode StartupBehaviorMode { get; }
    bool TriggersEnabled { get; }
    bool TriggerNotificationsEnabled { get; }
    CloseBehaviorMode CloseBehaviorMode { get; }
    bool TrayUseMonochromeInactiveIcon { get; }
    string TrayVisibleFeatureIds { get; }
    bool NotificationEnabled { get; }
    NotificationLevel NotificationLevel { get; }
    bool RestoreProxyOnExit { get; set; }
    bool CheckStaleProxyOnStartup { get; set; }
    bool StartupConflictCheckEnabled { get; set; }
    bool ShowStartupGuideOnStartup { get; set; }
    MainlandChinaFeatureMode MainlandChinaFeatureMode { get; }
    AppAccentColorMode AppAccentColorMode { get; }
    string AppAccentColorValue { get; }
}
