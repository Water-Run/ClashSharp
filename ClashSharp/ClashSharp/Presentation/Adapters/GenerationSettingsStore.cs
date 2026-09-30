using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Reads desired page choices and awaits complete authority commands, preserving failures and restart-only outcomes.</summary>
internal sealed class GenerationSettingsStore(ISettingsAuthority authority) : ISettingsStore
{
    private readonly ISettingsAuthority _authority = authority ?? throw new ArgumentNullException(nameof(authority));

    public AppLanguage DisplayLanguage => Read<AppLanguage>(SettingsRegistry.Keys.DisplayLanguage);

    public AppThemeMode AppThemeMode => Read<AppThemeMode>(SettingsRegistry.Keys.AppThemeMode);

    public AppAccentColorMode AppAccentColorMode => Read<AppAccentColorMode>(SettingsRegistry.Keys.AppAccentColorMode);

    public string AppAccentColorValue => Read<string>(SettingsRegistry.Keys.AppAccentColorValue);

    public bool LaunchAtStartupEnabled => Read<bool>(SettingsRegistry.Keys.LaunchAtStartupEnabled);

    public ClashSharpMode CurrentMode => Read<ClashSharpMode>(SettingsRegistry.Keys.CurrentMode);

    public string ActiveProfileId => Read<string>(SettingsRegistry.Keys.ActiveProfileId);

    public bool TransparentProxyEnabled => Read<bool>(SettingsRegistry.Keys.TransparentProxyEnabled);

    public int MixedPort => Read<int>(SettingsRegistry.Keys.MixedPort);

    public bool ConnectionSamplingEnabled => Read<bool>(SettingsRegistry.Keys.ConnectionSamplingEnabled);

    public int ConnectionSamplingIntervalSeconds => Read<int>(SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds);

    public bool StartupConflictCheckEnabled => Read<bool>(SettingsRegistry.Keys.StartupConflictCheckEnabled);

    public StartupBehaviorMode StartupBehaviorMode => Read<StartupBehaviorMode>(SettingsRegistry.Keys.StartupBehaviorMode);

    public bool ShowStartupGuideOnStartup => Read<bool>(SettingsRegistry.Keys.ShowStartupGuideOnStartup);

    public bool TriggersEnabled => Read<bool>(SettingsRegistry.Keys.TriggersEnabled);

    public bool TriggerNotificationsEnabled => Read<bool>(SettingsRegistry.Keys.TriggerNotificationsEnabled);

    public CloseBehaviorMode CloseBehaviorMode => Read<CloseBehaviorMode>(SettingsRegistry.Keys.CloseBehaviorMode);

    public bool TrayUseMonochromeInactiveIcon => Read<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon);

    public string TrayVisibleFeatureIds => Read<string>(SettingsRegistry.Keys.TrayVisibleFeatureIds);

    public bool CheckStaleProxyOnStartup => Read<bool>(SettingsRegistry.Keys.CheckStaleProxyOnStartup);

    public bool RestoreProxyOnExit => Read<bool>(SettingsRegistry.Keys.RestoreProxyOnExit);

    public MainlandChinaFeatureMode MainlandChinaFeatureMode => Read<MainlandChinaFeatureMode>(SettingsRegistry.Keys.MainlandChinaFeatureMode);

    public bool MainlandChinaUrlBlockingEnabled => Read<bool>(SettingsRegistry.Keys.MainlandChinaUrlBlockingEnabled);

    public bool NotificationEnabled => Read<bool>(SettingsRegistry.Keys.NotificationEnabled);

    public NotificationLevel NotificationLevel => Read<NotificationLevel>(SettingsRegistry.Keys.NotificationLevel);

    public string ConnectionTestProxyUrl1 => Read<string>(SettingsRegistry.Keys.ConnectionTestProxyUrl1);

    public string ConnectionTestProxyUrl2 => Read<string>(SettingsRegistry.Keys.ConnectionTestProxyUrl2);

    public string ConnectionTestDirectUrl => Read<string>(SettingsRegistry.Keys.ConnectionTestDirectUrl);

    public IReadOnlyList<SettingValueChange> ReadPreferenceChanges(IReadOnlyList<SettingKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        SettingKey[] requested = keys.ToArray();
        foreach (SettingKey key in requested) { _ = SettingsRegistry.Default.Get(key.Value); }
        SettingsEnvelope envelope = _authority.CaptureSnapshot().Envelope;
        return Array.AsReadOnly(requested.Select(key => new SettingValueChange(key, envelope.Desired[key].Value)).ToArray());
    }

    public ConnectionSamplingSettings ReadConnectionSamplingSettings()
    {
        SettingsEnvelope envelope = _authority.CaptureSnapshot().Envelope;
        return new(envelope.Desired[SettingsRegistry.Keys.ConnectionSamplingEnabled].Value.Get<bool>(),
            envelope.Desired[SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds].Value.Get<int>());
    }

    public async Task ApplyChangesAsync(IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        SettingValueChange[] snapshot = changes.ToArray();
        SettingsAuthorityResult result = await _authority.ApplyChangesAsync(snapshot, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (!result.IsSucceeded && result.Status != SettingsAuthorityStatus.DeferredToRestart)
        {
            throw new SettingsPageCommandException(result);
        }
        // A restart-only result accepts the durable choice; it never claims that pending effects are already applied.
        if (result.Envelope is null || snapshot.Any(change =>
            !result.Envelope.Desired.TryGetValue(change.Key, out SettingDesiredEntry? entry) || !entry.Value.Equals(change.Value)))
        {
            throw new InvalidOperationException("The settings command did not return its verified desired values.");
        }
    }

    public Task ResetPreferenceGroupAsync(SettingsResetScope scope, CancellationToken cancellationToken)
    {
        if (scope is not (SettingsResetScope.Basic or SettingsResetScope.Notifications
            or SettingsResetScope.Triggers or SettingsResetScope.Tray
            or SettingsResetScope.WindowsNative or SettingsResetScope.MainlandChina))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
        SettingValueChange[] defaults = SettingsRegistry.Default.GetResetDefinitions(scope)
            .Select(definition => new SettingValueChange(definition.Key, definition.DefaultValue)).ToArray();
        return ApplyChangesAsync(defaults, cancellationToken);
    }

    private T Read<T>(SettingKey key) where T : notnull =>
        _authority.CaptureSnapshot().Envelope.Desired[key].Value.Get<T>();
}
