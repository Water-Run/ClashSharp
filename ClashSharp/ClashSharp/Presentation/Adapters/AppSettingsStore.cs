using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;
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

    public Task ApplyChangesAsync(IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken) =>
        _settings.ApplyChangesAsync(changes, cancellationToken);

    public Task ResetPreferenceGroupAsync(SettingsResetScope scope, CancellationToken cancellationToken) =>
        _settings.ResetPreferenceGroupAsync(scope, cancellationToken);

    public IReadOnlyList<SettingValueChange> ReadPreferenceChanges(IReadOnlyList<SettingKey> keys) =>
        _settings.ReadPreferenceChanges(keys);

    public AppLanguage DisplayLanguage
    {
        get => _settings.DisplayLanguage;
    }

    public bool TransparentProxyEnabled
    {
        get => _settings.TransparentProxyEnabled;
    }

    public AppThemeMode AppThemeMode
    {
        get => _settings.AppThemeMode;
    }

    public AppAccentColorMode AppAccentColorMode
    {
        get => _settings.AppAccentColorMode;
    }

    public string AppAccentColorValue
    {
        get => _settings.AppAccentColorValue;
    }

    public bool LaunchAtStartupEnabled
    {
        get => _settings.LaunchAtStartupEnabled;
    }

    public ClashSharpMode CurrentMode
    {
        get => _settings.CurrentMode;
    }

    public string ActiveProfileId
    {
        get => _settings.ActiveProfileId;
    }

    public int MixedPort
    {
        get => _settings.MixedPort;
    }

    public bool ConnectionSamplingEnabled
    {
        get => _settings.ConnectionSamplingEnabled;
    }

    public int ConnectionSamplingIntervalSeconds
    {
        get => _settings.ConnectionSamplingIntervalSeconds;
    }

    public ConnectionSamplingSettings ReadConnectionSamplingSettings() => _settings.ReadConnectionSamplingSettings();

    public bool StartupConflictCheckEnabled
    {
        get => _settings.StartupConflictCheckEnabled;
    }

    public StartupBehaviorMode StartupBehaviorMode
    {
        get => _settings.StartupBehaviorMode;
    }

    public bool ShowStartupGuideOnStartup
    {
        get => _settings.ShowStartupGuideOnStartup;
    }

    public bool TriggersEnabled
    {
        get => _settings.TriggersEnabled;
    }

    public bool TriggerNotificationsEnabled
    {
        get => _settings.TriggerNotificationsEnabled;
    }

    public CloseBehaviorMode CloseBehaviorMode
    {
        get => _settings.CloseBehaviorMode;
    }

    public bool TrayUseMonochromeInactiveIcon
    {
        get => _settings.TrayUseMonochromeInactiveIcon;
    }

    public string TrayVisibleFeatureIds
    {
        get => _settings.TrayVisibleFeatureIds;
    }

    public bool CheckStaleProxyOnStartup
    {
        get => _settings.CheckStaleProxyOnStartup;
    }

    public bool RestoreProxyOnExit
    {
        get => _settings.RestoreProxyOnExit;
    }

    public MainlandChinaFeatureMode MainlandChinaFeatureMode
    {
        get => _settings.MainlandChinaFeatureMode;
    }

    public bool MainlandChinaUrlBlockingEnabled
    {
        get => _settings.MainlandChinaUrlBlockingEnabled;
    }

    public bool NotificationEnabled
    {
        get => _settings.NotificationEnabled;
    }

    public NotificationLevel NotificationLevel
    {
        get => _settings.NotificationLevel;
    }

    public string ConnectionTestProxyUrl1 => _settings.ConnectionTestProxyUrl1;

    public string ConnectionTestProxyUrl2 => _settings.ConnectionTestProxyUrl2;

    public string ConnectionTestDirectUrl => _settings.ConnectionTestDirectUrl;

}
