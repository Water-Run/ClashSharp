using System;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Service;

/// <summary>Projects one immutable authority snapshot into the compatible package format without any settings writer.</summary>
internal sealed class DataPackageSettingsSnapshot(SettingsEnvelope envelope) : IClashDataPackageSettings
{
    private readonly SettingsEnvelope _envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
    private T Read<T>(string key) where T : notnull => _envelope.Desired[SettingsRegistry.Default.Get(key).Key].Value.Get<T>();
    private static InvalidOperationException ReadOnly() => new("A package snapshot cannot change application settings.");

    public void ResetAllSettings() => throw ReadOnly();
    public AppLanguage DisplayLanguage { get => Read<AppLanguage>(nameof(DisplayLanguage)); set => throw ReadOnly(); }
    public AppThemeMode AppThemeMode { get => Read<AppThemeMode>(nameof(AppThemeMode)); set => throw ReadOnly(); }
    public AppAccentColorMode AppAccentColorMode { get => Read<AppAccentColorMode>(nameof(AppAccentColorMode)); set => throw ReadOnly(); }
    public string AppAccentColorValue { get => Read<string>(nameof(AppAccentColorValue)); set => throw ReadOnly(); }
    public bool LaunchAtStartupEnabled { get => Read<bool>(nameof(LaunchAtStartupEnabled)); set => throw ReadOnly(); }
    public ClashSharpMode CurrentMode { get => Read<ClashSharpMode>(nameof(CurrentMode)); set => throw ReadOnly(); }
    public string ActiveProfileId { get => Read<string>(nameof(ActiveProfileId)); set => throw ReadOnly(); }
    public bool TransparentProxyEnabled { get => Read<bool>(nameof(TransparentProxyEnabled)); set => throw ReadOnly(); }
    public int MixedPort { get => Read<int>(nameof(MixedPort)); set => throw ReadOnly(); }
    public bool ConnectionSamplingEnabled { get => Read<bool>(nameof(ConnectionSamplingEnabled)); set => throw ReadOnly(); }
    public int ConnectionSamplingIntervalSeconds { get => Read<int>(nameof(ConnectionSamplingIntervalSeconds)); set => throw ReadOnly(); }
    public bool RestoreProxyOnExit { get => Read<bool>(nameof(RestoreProxyOnExit)); set => throw ReadOnly(); }
    public bool CheckStaleProxyOnStartup { get => Read<bool>(nameof(CheckStaleProxyOnStartup)); set => throw ReadOnly(); }
    public bool StartupConflictCheckEnabled { get => Read<bool>(nameof(StartupConflictCheckEnabled)); set => throw ReadOnly(); }
    public StartupBehaviorMode StartupBehaviorMode { get => Read<StartupBehaviorMode>(nameof(StartupBehaviorMode)); set => throw ReadOnly(); }
    public bool ShowStartupGuideOnStartup { get => Read<bool>(nameof(ShowStartupGuideOnStartup)); set => throw ReadOnly(); }
    public bool TriggersEnabled { get => Read<bool>(nameof(TriggersEnabled)); set => throw ReadOnly(); }
    public bool TriggerNotificationsEnabled { get => Read<bool>(nameof(TriggerNotificationsEnabled)); set => throw ReadOnly(); }
    public CloseBehaviorMode CloseBehaviorMode { get => Read<CloseBehaviorMode>(nameof(CloseBehaviorMode)); set => throw ReadOnly(); }
    public bool TrayUseMonochromeInactiveIcon { get => Read<bool>(nameof(TrayUseMonochromeInactiveIcon)); set => throw ReadOnly(); }
    public string TrayVisibleFeatureIds { get => Read<string>(nameof(TrayVisibleFeatureIds)); set => throw ReadOnly(); }
    public bool NotificationEnabled { get => Read<bool>(nameof(NotificationEnabled)); set => throw ReadOnly(); }
    public NotificationLevel NotificationLevel { get => Read<NotificationLevel>(nameof(NotificationLevel)); set => throw ReadOnly(); }
    public MainlandChinaFeatureMode MainlandChinaFeatureMode { get => Read<MainlandChinaFeatureMode>(nameof(MainlandChinaFeatureMode)); set => throw ReadOnly(); }
    public bool MainlandChinaUrlBlockingEnabled { get => Read<bool>(nameof(MainlandChinaUrlBlockingEnabled)); set => throw ReadOnly(); }
    public string ConnectionTestUrl { get => Read<string>(nameof(ConnectionTestUrl)); set => throw ReadOnly(); }
    public string ConnectionTestProxyUrl1 { get => Read<string>(nameof(ConnectionTestProxyUrl1)); set => throw ReadOnly(); }
    public string ConnectionTestProxyUrl2 { get => Read<string>(nameof(ConnectionTestProxyUrl2)); set => throw ReadOnly(); }
    public string ConnectionTestDirectUrl { get => Read<string>(nameof(ConnectionTestDirectUrl)); set => throw ReadOnly(); }
    public string MasterHeroStatusLayout { get => Read<string>(nameof(MasterHeroStatusLayout)); set => throw ReadOnly(); }
    public string MasterInfoTileLayout { get => Read<string>(nameof(MasterInfoTileLayout)); set => throw ReadOnly(); }
}
