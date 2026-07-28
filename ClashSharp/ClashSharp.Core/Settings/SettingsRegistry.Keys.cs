namespace ClashSharp.Settings;

public sealed partial class SettingsRegistry
{
    /// <summary>Provides typed stable keys without duplicating persisted key text in consumers.</summary>
    public static class Keys
    {
        /// <summary>Selected display language.</summary>
        public static SettingKey DisplayLanguage { get; } = new("DisplayLanguage");

        /// <summary>Selected application theme.</summary>
        public static SettingKey AppThemeMode { get; } = new("AppThemeMode");

        /// <summary>Selected accent-color behavior.</summary>
        public static SettingKey AppAccentColorMode { get; } = new("AppAccentColorMode");

        /// <summary>Custom accent-color value.</summary>
        public static SettingKey AppAccentColorValue { get; } = new("AppAccentColorValue");

        /// <summary>Requested Windows StartupTask state.</summary>
        public static SettingKey LaunchAtStartupEnabled { get; } = new("LaunchAtStartupEnabled");

        /// <summary>Desired network takeover mode.</summary>
        public static SettingKey CurrentMode { get; } = new("CurrentMode");

        /// <summary>Desired active profile identifier.</summary>
        public static SettingKey ActiveProfileId { get; } = new("ActiveProfileId");

        /// <summary>Desired transparent-proxy state.</summary>
        public static SettingKey TransparentProxyEnabled { get; } = new("TransparentProxyEnabled");

        /// <summary>Desired mixed HTTP and SOCKS port.</summary>
        public static SettingKey MixedPort { get; } = new("MixedPort");

        /// <summary>Desired connection-sampling state.</summary>
        public static SettingKey ConnectionSamplingEnabled { get; } = new("ConnectionSamplingEnabled");

        /// <summary>Desired connection-sampling interval.</summary>
        public static SettingKey ConnectionSamplingIntervalSeconds { get; } =
            new("ConnectionSamplingIntervalSeconds");

        /// <summary>Normal-exit Windows proxy restoration policy.</summary>
        public static SettingKey RestoreProxyOnExit { get; } = new("RestoreProxyOnExit");

        /// <summary>Startup stale-proxy detection policy.</summary>
        public static SettingKey CheckStaleProxyOnStartup { get; } = new("CheckStaleProxyOnStartup");

        /// <summary>Startup conflict-detection policy.</summary>
        public static SettingKey StartupConflictCheckEnabled { get; } = new("StartupConflictCheckEnabled");

        /// <summary>Startup network behavior.</summary>
        public static SettingKey StartupBehaviorMode { get; } = new("StartupBehaviorMode");

        /// <summary>Startup-guide visibility policy.</summary>
        public static SettingKey ShowStartupGuideOnStartup { get; } = new("ShowStartupGuideOnStartup");

        /// <summary>Desired trigger scheduler state.</summary>
        public static SettingKey TriggersEnabled { get; } = new("TriggersEnabled");

        /// <summary>Trigger-notification policy.</summary>
        public static SettingKey TriggerNotificationsEnabled { get; } = new("TriggerNotificationsEnabled");

        /// <summary>Main-window close behavior.</summary>
        public static SettingKey CloseBehaviorMode { get; } = new("CloseBehaviorMode");

        /// <summary>Inactive notification-area icon color policy.</summary>
        public static SettingKey TrayUseMonochromeInactiveIcon { get; } =
            new("TrayUseMonochromeInactiveIcon");

        /// <summary>Visible notification-area feature identifiers.</summary>
        public static SettingKey TrayVisibleFeatureIds { get; } = new("TrayVisibleFeatureIds");

        /// <summary>Mainland China presentation-policy level.</summary>
        public static SettingKey MainlandChinaFeatureMode { get; } = new("MainlandChinaFeatureMode");

        /// <summary>Legacy combined mainland China display flag.</summary>
        public static SettingKey MainlandChinaDisplayEnabled { get; } = new("MainlandChinaDisplayEnabled");

        /// <summary>Mainland China URL-masking policy.</summary>
        public static SettingKey MainlandChinaUrlBlockingEnabled { get; } =
            new("MainlandChinaUrlBlockingEnabled");

        /// <summary>Windows system-notification state.</summary>
        public static SettingKey NotificationEnabled { get; } = new("NotificationEnabled");

        /// <summary>Windows system-notification verbosity.</summary>
        public static SettingKey NotificationLevel { get; } = new("NotificationLevel");

        /// <summary>Legacy primary proxy connection-test URL.</summary>
        public static SettingKey ConnectionTestUrl { get; } = new("ConnectionTestUrl");

        /// <summary>Master-control hero status layout.</summary>
        public static SettingKey MasterHeroStatusLayout { get; } = new("MasterHeroStatusLayout");

        /// <summary>Master-control visible information-tile layout.</summary>
        public static SettingKey MasterInfoTileLayout { get; } = new("MasterInfoTileLayout");

        /// <summary>First proxy-routed connection-test URL.</summary>
        public static SettingKey ConnectionTestProxyUrl1 { get; } = new("ConnectionTestProxyUrl1");

        /// <summary>Second proxy-routed connection-test URL.</summary>
        public static SettingKey ConnectionTestProxyUrl2 { get; } = new("ConnectionTestProxyUrl2");

        /// <summary>Direct connection-test URL.</summary>
        public static SettingKey ConnectionTestDirectUrl { get; } = new("ConnectionTestDirectUrl");
    }
}
