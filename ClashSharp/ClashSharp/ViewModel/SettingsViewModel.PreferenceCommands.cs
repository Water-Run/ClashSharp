using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

internal sealed partial class SettingsViewModel
{
    private static readonly HashSet<SettingKey> AsyncPreferenceKeys =
    [
        SettingsRegistry.Keys.DisplayLanguage, SettingsRegistry.Keys.AppThemeMode,
        SettingsRegistry.Keys.AppAccentColorMode, SettingsRegistry.Keys.AppAccentColorValue,
        SettingsRegistry.Keys.StartupConflictCheckEnabled, SettingsRegistry.Keys.StartupBehaviorMode,
        SettingsRegistry.Keys.ShowStartupGuideOnStartup, SettingsRegistry.Keys.CloseBehaviorMode,
        SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon, SettingsRegistry.Keys.TrayVisibleFeatureIds,
        SettingsRegistry.Keys.CheckStaleProxyOnStartup, SettingsRegistry.Keys.RestoreProxyOnExit,
        SettingsRegistry.Keys.MainlandChinaFeatureMode, SettingsRegistry.Keys.MainlandChinaUrlBlockingEnabled,
        SettingsRegistry.Keys.NotificationEnabled, SettingsRegistry.Keys.NotificationLevel,
        SettingsRegistry.Keys.TriggersEnabled, SettingsRegistry.Keys.TriggerNotificationsEnabled,
        SettingsRegistry.Keys.ConnectionTestProxyUrl1, SettingsRegistry.Keys.ConnectionTestProxyUrl2,
        SettingsRegistry.Keys.ConnectionTestDirectUrl,
    ];
    private int _preferenceCommands;

    /// <summary>Gets whether the page can accept another preference edit.</summary>
    public bool IsPreferenceInputEnabled => _preferenceCommands == 0;

    /// <summary>Normalizes an immutable user choice before entering the asynchronous command boundary.</summary>
    public Task ApplyPreferenceAsync<T>(SettingKey key, T value, CancellationToken cancellationToken) where T : notnull
        => ApplyPreferenceChangesAsync([CreatePreferenceChange(key, value)], cancellationToken);

    /// <summary>Commits both color-picker values together before notifying appearance observers.</summary>
    public Task ApplyCustomAccentColorAsync(string value, CancellationToken cancellationToken) => ApplyPreferenceChangesAsync(
        [CreatePreferenceChange(SettingsRegistry.Keys.AppAccentColorMode, AppAccentColorMode.Custom),
         CreatePreferenceChange(SettingsRegistry.Keys.AppAccentColorValue, value)], cancellationToken);

    /// <summary>Preserves the tray editor's default menu when its selection is empty.</summary>
    public Task ApplyTrayVisibleFeatureIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ApplyPreferenceAsync(SettingsRegistry.Keys.TrayVisibleFeatureIds, NormalizeTrayVisibleFeatureIds(ids), cancellationToken);
    }

    /// <summary>Validates and commits all connection-test destinations as one request.</summary>
    public Task ApplyConnectionTestUrlsAsync(string proxyUrl1, string proxyUrl2, string directUrl, CancellationToken cancellationToken)
        => ApplyPreferenceChangesAsync(
            [CreatePreferenceChange(SettingsRegistry.Keys.ConnectionTestProxyUrl1, proxyUrl1),
             CreatePreferenceChange(SettingsRegistry.Keys.ConnectionTestProxyUrl2, proxyUrl2),
             CreatePreferenceChange(SettingsRegistry.Keys.ConnectionTestDirectUrl, directUrl)], cancellationToken);

    /// <summary>Resets one preference-only group without replacing unrelated staged or restart state.</summary>
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
        return ExecutePreferenceCommandAsync(
            defaults, token => _settings.ResetPreferenceGroupAsync(scope, token), cancellationToken);
    }

    private static SettingValueChange CreatePreferenceChange<T>(SettingKey key, T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        SettingNormalizationResult normalized = SettingsRegistry.Default.Get(key.Value).NormalizeValue(value);
        if (!normalized.IsSuccess) { throw new ArgumentException(normalized.Error!.Code, nameof(value)); }
        return new(key, normalized.Value!);
    }

    /// <summary>Waits for the complete preference command before projecting its committed result.</summary>
    public Task ApplyPreferenceChangesAsync(IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        SettingValueChange[] snapshot = changes.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(change => change is null || !AsyncPreferenceKeys.Contains(change.Key))
            || snapshot.Select(change => change.Key).Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("The page preference command contains an unsupported key.", nameof(changes));
        }
        foreach (SettingValueChange change in snapshot)
        {
            SettingNormalizationResult normalized = SettingsRegistry.Default.Get(change.Key.Value).Normalize(change.Value.CanonicalText);
            if (!normalized.IsSuccess || !change.Value.Equals(normalized.Value))
            {
                throw new ArgumentException("The page preference command contains an invalid value.", nameof(changes));
            }
        }
        return ExecutePreferenceCommandAsync(snapshot, token => _settings.ApplyChangesAsync(snapshot, token), cancellationToken);
    }

    private async Task ExecutePreferenceCommandAsync(
        IReadOnlyList<SettingValueChange> snapshot, Func<CancellationToken, Task> apply, CancellationToken cancellationToken)
    {
        _preferenceCommands++;
        try
        {
            OnPropertyChanged(nameof(IsPreferenceInputEnabled));
            OperationErrorText = string.Empty;
            await apply(cancellationToken);
            PublishPreferenceChanges(snapshot);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            if (!ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
            {
                OperationErrorText = _getString("Application.UnexpectedError");
            }
            try
            {
                // A write can commit and then fail while notifying consumers. Re-read its actual
                // values without claiming that any failed appearance effect has been applied.
                PublishPreferenceChanges(_settings.ReadPreferenceChanges(snapshot.Select(change => change.Key).ToArray()), applyTheme: false);
            }
            catch (Exception readFailure) when (!ExceptionGraphClassifier.IsProcessFatal(readFailure))
            {
                OperationErrorText = _getString("Application.UnexpectedError");
                throw new AggregateException(exception, readFailure);
            }
            throw;
        }
        finally
        {
            _preferenceCommands--;
            OnPropertyChanged(nameof(IsPreferenceInputEnabled));
        }
    }

    private void PublishPreferenceChanges(IReadOnlyList<SettingValueChange> changes, bool applyTheme = true)
    {
        // Install every field before any observer can read a paired value from this batch.
        HashSet<string> notifications = new(StringComparer.Ordinal);
        foreach (SettingValueChange change in changes)
        {
            switch (change.Key.Value)
            {
                case nameof(DisplayLanguage):
                    Stage(ref _displayLanguage, change.Value, nameof(DisplayLanguage), nameof(DisplayLanguageIndex));
                    break;
                case nameof(AppThemeMode):
                    Stage(ref _appThemeMode, change.Value, nameof(AppThemeMode), nameof(AppThemeModeIndex));
                    break;
                case nameof(AppAccentColorMode):
                    Stage(ref _appAccentColorMode, change.Value, nameof(AppAccentColorMode), nameof(AppAccentColorModeIndex), nameof(IsCustomAccentColorSelected));
                    break;
                case nameof(AppAccentColorValue):
                    Stage(ref _appAccentColorValue, change.Value, nameof(AppAccentColorValue));
                    break;
                case nameof(StartupConflictCheckEnabled):
                    Stage(ref _startupConflictCheckEnabled, change.Value, nameof(StartupConflictCheckEnabled));
                    break;
                case nameof(StartupBehaviorMode):
                    Stage(ref _startupBehaviorMode, change.Value, nameof(StartupBehaviorMode), nameof(StartupBehaviorModeIndex));
                    break;
                case nameof(ShowStartupGuideOnStartup):
                    Stage(ref _showStartupGuideOnStartup, change.Value, nameof(ShowStartupGuideOnStartup));
                    break;
                case nameof(CloseBehaviorMode):
                    Stage(ref _closeBehaviorMode, change.Value, nameof(CloseBehaviorMode), nameof(CloseBehaviorModeIndex));
                    break;
                case nameof(TrayUseMonochromeInactiveIcon):
                    Stage(ref _trayUseMonochromeInactiveIcon, change.Value, nameof(TrayUseMonochromeInactiveIcon));
                    break;
                case nameof(TrayVisibleFeatureIds):
                    Stage(ref _trayVisibleFeatureIds, change.Value, nameof(TrayVisibleFeatureIds), nameof(TrayVisibleFeatureSummaryText));
                    break;
                case nameof(CheckStaleProxyOnStartup):
                    Stage(ref _checkStaleProxyOnStartup, change.Value, nameof(CheckStaleProxyOnStartup));
                    break;
                case nameof(RestoreProxyOnExit):
                    Stage(ref _restoreProxyOnExit, change.Value, nameof(RestoreProxyOnExit));
                    break;
                case nameof(MainlandChinaFeatureMode):
                    Stage(ref _mainlandChinaFeatureMode, change.Value, nameof(MainlandChinaFeatureMode), nameof(MainlandChinaFeatureModeIndex));
                    break;
                case nameof(MainlandChinaUrlBlockingEnabled):
                    Stage(ref _mainlandChinaUrlBlockingEnabled, change.Value, nameof(MainlandChinaUrlBlockingEnabled));
                    break;
                case nameof(NotificationEnabled):
                    Stage(ref _notificationEnabled, change.Value, nameof(NotificationEnabled));
                    break;
                case nameof(NotificationLevel):
                    Stage(ref _notificationLevel, change.Value, nameof(NotificationLevel), nameof(NotificationLevelIndex));
                    break;
                case nameof(TriggersEnabled):
                    Stage(ref _triggersEnabled, change.Value, nameof(TriggersEnabled));
                    break;
                case nameof(TriggerNotificationsEnabled):
                    Stage(ref _triggerNotificationsEnabled, change.Value, nameof(TriggerNotificationsEnabled));
                    break;
                case nameof(ConnectionTestProxyUrl1):
                    Stage(ref _connectionTestProxyUrl1, change.Value, nameof(ConnectionTestProxyUrl1), nameof(ConnectionTestUrlSummaryText));
                    break;
                case nameof(ConnectionTestProxyUrl2):
                    Stage(ref _connectionTestProxyUrl2, change.Value, nameof(ConnectionTestProxyUrl2), nameof(ConnectionTestUrlSummaryText));
                    break;
                case nameof(ConnectionTestDirectUrl):
                    Stage(ref _connectionTestDirectUrl, change.Value, nameof(ConnectionTestDirectUrl), nameof(ConnectionTestUrlSummaryText));
                    break;
            }
        }
        foreach (string property in notifications) { OnPropertyChanged(property); }
        if (notifications.Contains(nameof(DisplayLanguage))) { RaiseDisplayLanguageRestartStateChanged(); }
        if (notifications.Contains(nameof(AppAccentColorMode)) || notifications.Contains(nameof(AppAccentColorValue)))
        {
            RaiseAppAccentColorRestartStateChanged();
        }
        if (notifications.Contains(nameof(MainlandChinaFeatureMode)) || notifications.Contains(nameof(MainlandChinaUrlBlockingEnabled)))
        {
            RaiseMainlandChinaRestartStateChanged();
        }
        if (notifications.Contains(nameof(TriggersEnabled))) { RaiseTriggerRestartStateChanged(); }
        if (applyTheme && changes.Any(change => change.Key == SettingsRegistry.Keys.AppThemeMode)) { _applyTheme(AppThemeMode); }

        void Stage<T>(ref T field, SettingValue value, string property, params string[] related) where T : notnull
        {
            T next = value.Get<T>();
            if (EqualityComparer<T>.Default.Equals(field, next)) { return; }
            field = next;
            notifications.Add(property);
            foreach (string name in related) { notifications.Add(name); }
        }
    }
}
