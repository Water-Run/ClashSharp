using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

/// <summary>Minimal storage contract required by <see cref="SettingsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations persist valid values immediately.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: Property setters may write to durable user settings.
/// </remarks>
internal interface ISettingsStore
{
    /// <summary>Resets one supported preference group as a complete batch before notifying observers.</summary>
    void ResetPreferenceGroup(SettingsResetScope scope);

    AppLanguage DisplayLanguage { get; set; }

    AppThemeMode AppThemeMode { get; set; }

    AppAccentColorMode AppAccentColorMode { get; set; }

    string AppAccentColorValue { get; set; }

    /// <summary>Commits custom mode and color together and returns the normalized persisted color.</summary>
    string SetCustomAppAccentColor(string value);

    bool LaunchAtStartupEnabled { get; set; }

    ClashSharpMode CurrentMode { get; set; }

    string ActiveProfileId { get; set; }

    bool TransparentProxyEnabled { get; set; }

    int MixedPort { get; set; }

    bool ConnectionSamplingEnabled { get; set; }

    int ConnectionSamplingIntervalSeconds { get; set; }

    bool StartupConflictCheckEnabled { get; set; }

    StartupBehaviorMode StartupBehaviorMode { get; set; }

    bool ShowStartupGuideOnStartup { get; set; }

    bool TriggersEnabled { get; set; }

    bool TriggerNotificationsEnabled { get; set; }

    CloseBehaviorMode CloseBehaviorMode { get; set; }

    bool TrayUseMonochromeInactiveIcon { get; set; }

    string TrayVisibleFeatureIds { get; set; }

    bool CheckStaleProxyOnStartup { get; set; }

    bool RestoreProxyOnExit { get; set; }

    MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; }

    bool MainlandChinaUrlBlockingEnabled { get; set; }

    bool NotificationEnabled { get; set; }

    NotificationLevel NotificationLevel { get; set; }

    string ConnectionTestUrl { get; set; }

    string ConnectionTestProxyUrl1 { get; }

    string ConnectionTestProxyUrl2 { get; }

    string ConnectionTestDirectUrl { get; }

    /// <summary>Persists all three connection-test targets in one admitted, rollback-capable batch.</summary>
    void SetConnectionTestUrls(string proxyUrl1, string proxyUrl2, string directUrl);
}
