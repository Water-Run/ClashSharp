using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

/// <summary>Minimal storage contract required by <see cref="SettingsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Change commands complete before committed values are published.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: Writes occur only through asynchronous change and reset commands.
/// </remarks>
internal interface ISettingsStore
{
    /// <summary>Awaits one immutable, canonical preference change set before the page publishes its result.</summary>
    Task ApplyChangesAsync(IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken);

    /// <summary>Awaits a complete preference-group reset before publishing its defaults.</summary>
    Task ResetPreferenceGroupAsync(SettingsResetScope scope, CancellationToken cancellationToken);

    /// <summary>Reads a coherent committed snapshot to reconcile a command that returned an error.</summary>
    IReadOnlyList<SettingValueChange> ReadPreferenceChanges(IReadOnlyList<SettingKey> keys);

    AppLanguage DisplayLanguage { get; }

    AppThemeMode AppThemeMode { get; }

    AppAccentColorMode AppAccentColorMode { get; }

    string AppAccentColorValue { get; }

    bool LaunchAtStartupEnabled { get; }

    ClashSharpMode CurrentMode { get; }

    string ActiveProfileId { get; }

    bool TransparentProxyEnabled { get; }

    int MixedPort { get; }

    bool ConnectionSamplingEnabled { get; }

    int ConnectionSamplingIntervalSeconds { get; }

    /// <summary>Reads the complete sampling preference pair from one authority snapshot.</summary>
    ConnectionSamplingSettings ReadConnectionSamplingSettings();

    bool StartupConflictCheckEnabled { get; }

    StartupBehaviorMode StartupBehaviorMode { get; }

    bool ShowStartupGuideOnStartup { get; }

    bool TriggersEnabled { get; }

    bool TriggerNotificationsEnabled { get; }

    CloseBehaviorMode CloseBehaviorMode { get; }

    bool TrayUseMonochromeInactiveIcon { get; }

    string TrayVisibleFeatureIds { get; }

    bool CheckStaleProxyOnStartup { get; }

    bool RestoreProxyOnExit { get; }

    MainlandChinaFeatureMode MainlandChinaFeatureMode { get; }

    bool MainlandChinaUrlBlockingEnabled { get; }

    bool NotificationEnabled { get; }

    NotificationLevel NotificationLevel { get; }

    string ConnectionTestProxyUrl1 { get; }

    string ConnectionTestProxyUrl2 { get; }

    string ConnectionTestDirectUrl { get; }

}
