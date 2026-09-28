using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="AppSettingsService"/> to master-control settings.</summary>
/// <remarks>
/// Invariants: Wraps a non-null settings service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Awaitable change sets persist values through the wrapped service.
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

    public Task ApplyChangesAsync(IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken) =>
        _settings.ApplyChangesAsync(changes, cancellationToken);

    /// <summary>Gets the current master takeover mode.</summary>
    /// <value>Current persisted mode.</value>
    public ClashSharpMode CurrentMode => _settings.CurrentMode;

    /// <summary>Gets whether transparent proxy is enabled in settings.</summary>
    /// <value>True when transparent proxy is enabled; otherwise false.</value>
    public bool TransparentProxyEnabled => _settings.TransparentProxyEnabled;

    public bool LaunchAtStartupEnabled => _settings.LaunchAtStartupEnabled;

    public bool ConnectionSamplingEnabled => _settings.ConnectionSamplingEnabled;

    public bool MainlandChinaUrlBlockingEnabled => _settings.MainlandChinaUrlBlockingEnabled;

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

    public bool RestoreProxyOnExit => _settings.RestoreProxyOnExit;

    public bool CheckStaleProxyOnStartup => _settings.CheckStaleProxyOnStartup;

    public bool StartupConflictCheckEnabled => _settings.StartupConflictCheckEnabled;

    public bool ShowStartupGuideOnStartup => _settings.ShowStartupGuideOnStartup;

    public MainlandChinaFeatureMode MainlandChinaFeatureMode => _settings.MainlandChinaFeatureMode;

    public AppAccentColorMode AppAccentColorMode => _settings.AppAccentColorMode;

    public string AppAccentColorValue => _settings.AppAccentColorValue;
}
