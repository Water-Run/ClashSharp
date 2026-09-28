using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

/// <summary>Settings contract required by <see cref="MasterControlViewModel"/>.</summary>
internal interface IMasterControlSettings
{
    Task ApplyChangesAsync(IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken);

    ClashSharpMode CurrentMode { get; }
    bool TransparentProxyEnabled { get; }
    bool LaunchAtStartupEnabled { get; }
    bool ConnectionSamplingEnabled { get; }
    bool MainlandChinaUrlBlockingEnabled { get; }
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
    bool RestoreProxyOnExit { get; }
    bool CheckStaleProxyOnStartup { get; }
    bool StartupConflictCheckEnabled { get; }
    bool ShowStartupGuideOnStartup { get; }
    MainlandChinaFeatureMode MainlandChinaFeatureMode { get; }
    AppAccentColorMode AppAccentColorMode { get; }
    string AppAccentColorValue { get; }
}
