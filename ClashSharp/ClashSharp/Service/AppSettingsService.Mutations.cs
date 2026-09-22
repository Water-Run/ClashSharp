using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Service;

public sealed partial class AppSettingsService
{
    /// <summary>Reads the requested page preferences under the batch publication lock.</summary>
    internal IReadOnlyList<SettingValueChange> ReadPreferenceChanges(IReadOnlyList<SettingKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        SettingKey[] requested = keys.ToArray();
        lock (_syncLock)
        {
            return Array.AsReadOnly(requested.Select(key =>
            {
                object value = key.Value switch
                {
                    nameof(DisplayLanguage) => DisplayLanguage,
                    nameof(AppThemeMode) => AppThemeMode,
                    nameof(AppAccentColorMode) => AppAccentColorMode,
                    nameof(AppAccentColorValue) => AppAccentColorValue,
                    nameof(StartupConflictCheckEnabled) => StartupConflictCheckEnabled,
                    nameof(StartupBehaviorMode) => StartupBehaviorMode,
                    nameof(ShowStartupGuideOnStartup) => ShowStartupGuideOnStartup,
                    nameof(CloseBehaviorMode) => CloseBehaviorMode,
                    nameof(TrayUseMonochromeInactiveIcon) => TrayUseMonochromeInactiveIcon,
                    nameof(TrayVisibleFeatureIds) => TrayVisibleFeatureIds,
                    nameof(CheckStaleProxyOnStartup) => CheckStaleProxyOnStartup,
                    nameof(RestoreProxyOnExit) => RestoreProxyOnExit,
                    nameof(MainlandChinaFeatureMode) => MainlandChinaFeatureMode,
                    nameof(MainlandChinaUrlBlockingEnabled) => MainlandChinaUrlBlockingEnabled,
                    nameof(NotificationEnabled) => NotificationEnabled,
                    nameof(NotificationLevel) => NotificationLevel,
                    nameof(TriggersEnabled) => TriggersEnabled,
                    nameof(TriggerNotificationsEnabled) => TriggerNotificationsEnabled,
                    nameof(ConnectionTestProxyUrl1) => ConnectionTestProxyUrl1,
                    nameof(ConnectionTestProxyUrl2) => ConnectionTestProxyUrl2,
                    nameof(ConnectionTestDirectUrl) => ConnectionTestDirectUrl,
                    _ => throw new ArgumentException("The key is not a page preference.", nameof(keys)),
                };
                SettingNormalizationResult normalized = SettingsRegistry.Default.Get(key.Value).NormalizeValue(value);
                if (!normalized.IsSuccess) { throw new InvalidOperationException("A stored page preference cannot be normalized."); }
                return new SettingValueChange(key, normalized.Value!);
            }).ToArray());
        }
    }

    /// <summary>Resets one preference group under admission retained through all change notifications.</summary>
    internal async Task ResetPreferenceGroupAsync(SettingsResetScope scope, CancellationToken cancellationToken)
    {
        IReadOnlyList<SettingDefinition> definitions = GetPreferenceResetDefinitions(scope);
        MutationAdmissionBarrier admission = Volatile.Read(ref _mutationAdmission);
        using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        WriteAdmitted(lease, editor => editor.ResetDefinitions(definitions));
    }

    /// <summary>Copies and validates one canonical change set before obtaining write admission.</summary>
    internal async Task ApplyChangesAsync(IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        SettingValueChange[] snapshot = changes.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(change => change is null)
            || snapshot.Select(change => change.Key).Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("A preference change set must contain distinct canonical keys.", nameof(changes));
        }
        foreach (SettingValueChange change in snapshot)
        {
            SettingDefinition definition = SettingsRegistry.Default.Get(change.Key.Value);
            SettingNormalizationResult normalized = definition.Normalize(change.Value.CanonicalText);
            if (!normalized.IsSuccess || !change.Value.Equals(normalized.Value))
            {
                throw new ArgumentException("A preference change must match its canonical registry type and value.", nameof(changes));
            }
        }
        MutationAdmissionBarrier admission = Volatile.Read(ref _mutationAdmission);
        using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        WriteAdmitted(lease, editor =>
        {
            foreach (SettingValueChange change in snapshot) { editor.StageCanonical(change); }
        });
    }

    /// <summary>Reads the complete sampling preference pair under the same lock used by batch publication.</summary>
    internal ConnectionSamplingSettings ReadConnectionSamplingSettings()
    {
        lock (_syncLock)
        {
            return new ConnectionSamplingSettings(ConnectionSamplingEnabled, ConnectionSamplingIntervalSeconds);
        }
    }

    /// <summary>Executes one synchronous settings write under immediately acquired ordinary admission.</summary>
    private void WriteOrdinary(Action<AppSettingsEditor> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        MutationAdmissionBarrier admission = Volatile.Read(ref _mutationAdmission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        WriteAdmitted(lease, mutation);
    }

    /// <summary>Executes a validated, rollback-capable settings batch under an already-owned lease.</summary>
    internal void WriteAdmitted(
        MutationAdmissionLease admissionLease,
        Action<AppSettingsEditor> mutation)
    {
        ArgumentNullException.ThrowIfNull(admissionLease);
        ArgumentNullException.ThrowIfNull(mutation);

        MutationAdmissionBarrier admission = Volatile.Read(ref _mutationAdmission);
        admission.EnsureActiveLease(admissionLease);
        IReadOnlyList<AppSettingChangedEventArgs> changes;
        lock (_syncLock)
        {
            admission.EnsureActiveLease(admissionLease);
            AppSettingsEditor editor = new(this);
            try
            {
                mutation(editor);
                changes = editor.Commit();
            }
            finally
            {
                editor.Invalidate();
            }
        }

        foreach (AppSettingChangedEventArgs change in changes)
        {
            NotifySettingChanged(change);
        }
    }

    /// <summary>Stages and atomically applies a group of validated settings values.</summary>
    internal sealed class AppSettingsEditor : IClashDataPackageSettings
    {
        private readonly AppSettingsService _owner;
        private readonly Dictionary<string, object?> _pending = new(StringComparer.Ordinal);
        private bool _active = true;

        internal AppSettingsEditor(AppSettingsService owner)
        {
            _owner = owner;
        }

        public AppLanguage DisplayLanguage
        {
            get => _owner.DisplayLanguage;
            set => StageEnum(KeyDisplayLanguage, value);
        }

        public AppThemeMode AppThemeMode
        {
            get => _owner.AppThemeMode;
            set => StageEnum(KeyAppThemeMode, value);
        }

        public AppAccentColorMode AppAccentColorMode
        {
            get => _owner.AppAccentColorMode;
            set => StageEnum(KeyAppAccentColorMode, value);
        }

        public string AppAccentColorValue
        {
            get => _owner.AppAccentColorValue;
            set => Stage(KeyAppAccentColorValue, NormalizeAccentColorValue(value));
        }

        public bool LaunchAtStartupEnabled
        {
            get => _owner.LaunchAtStartupEnabled;
            set => Stage(KeyLaunchAtStartupEnabled, value);
        }

        public ClashSharpMode CurrentMode
        {
            get => _owner.CurrentMode;
            set => StageEnum(KeyCurrentMode, value);
        }

        public string ActiveProfileId
        {
            get => _owner.ActiveProfileId;
            set => Stage(KeyActiveProfileId, value ?? throw new ArgumentNullException(nameof(value)));
        }

        public bool TransparentProxyEnabled
        {
            get => _owner.TransparentProxyEnabled;
            set => Stage(KeyTransparentProxyEnabled, value);
        }

        public int MixedPort
        {
            get => _owner.MixedPort;
            set
            {
                if (value is < 1 or > 65535)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Port must be in the range [1, 65535].");
                }

                Stage(KeyMixedPort, value);
            }
        }

        public bool ConnectionSamplingEnabled
        {
            get => _owner.ConnectionSamplingEnabled;
            set => Stage(KeyConnectionSamplingEnabled, value);
        }

        public int ConnectionSamplingIntervalSeconds
        {
            get => _owner.ConnectionSamplingIntervalSeconds;
            set
            {
                if (value is < 3 or > 300)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        "Sampling interval must be in the range [3, 300] seconds.");
                }

                Stage(KeyConnectionSamplingIntervalSeconds, value);
            }
        }

        public bool RestoreProxyOnExit
        {
            get => _owner.RestoreProxyOnExit;
            set => Stage(KeyRestoreProxyOnExit, value);
        }

        public bool CheckStaleProxyOnStartup
        {
            get => _owner.CheckStaleProxyOnStartup;
            set => Stage(KeyCheckStaleProxyOnStartup, value);
        }

        public bool StartupConflictCheckEnabled
        {
            get => _owner.StartupConflictCheckEnabled;
            set => Stage(KeyStartupConflictCheckEnabled, value);
        }

        public StartupBehaviorMode StartupBehaviorMode
        {
            get => _owner.StartupBehaviorMode;
            set => StageEnum(KeyStartupBehaviorMode, value);
        }

        public bool ShowStartupGuideOnStartup
        {
            get => _owner.ShowStartupGuideOnStartup;
            set => Stage(KeyShowStartupGuideOnStartup, value);
        }

        public bool TriggersEnabled
        {
            get => _owner.TriggersEnabled;
            set => Stage(KeyTriggersEnabled, value);
        }

        public bool TriggerNotificationsEnabled
        {
            get => _owner.TriggerNotificationsEnabled;
            set => Stage(KeyTriggerNotificationsEnabled, value);
        }

        public CloseBehaviorMode CloseBehaviorMode
        {
            get => _owner.CloseBehaviorMode;
            set => StageEnum(KeyCloseBehaviorMode, value);
        }

        public bool TrayUseMonochromeInactiveIcon
        {
            get => _owner.TrayUseMonochromeInactiveIcon;
            set => Stage(KeyTrayUseMonochromeInactiveIcon, value);
        }

        public string TrayVisibleFeatureIds
        {
            get => _owner.TrayVisibleFeatureIds;
            set => Stage(KeyTrayVisibleFeatureIds, NormalizeTrayVisibleFeatureIds(value));
        }

        public bool NotificationEnabled
        {
            get => _owner.NotificationEnabled;
            set => Stage(KeyNotificationEnabled, value);
        }

        public NotificationLevel NotificationLevel
        {
            get => _owner.NotificationLevel;
            set => StageEnum(KeyNotificationLevel, value);
        }

        public MainlandChinaFeatureMode MainlandChinaFeatureMode
        {
            get => _owner.MainlandChinaFeatureMode;
            set
            {
                MainlandChinaFeatureMode persistedMode =
                    value == MainlandChinaFeatureMode.AllIncludingUrlBlacklist
                        ? MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter
                        : value;
                StageEnum(KeyMainlandChinaFeatureMode, persistedMode);
                Stage(
                    KeyMainlandChinaDisplayEnabled,
                    persistedMode != MainlandChinaFeatureMode.Disabled);
            }
        }

        internal bool MainlandChinaDisplayEnabled
        {
            set => MainlandChinaFeatureMode = value
                ? MainlandChinaFeatureMode.FlagReplacementAndTextCompletion
                : MainlandChinaFeatureMode.Disabled;
        }

        public bool MainlandChinaUrlBlockingEnabled
        {
            get => _owner.MainlandChinaUrlBlockingEnabled;
            set => Stage(KeyMainlandChinaUrlBlockingEnabled, value);
        }

        public string ConnectionTestUrl
        {
            get => _owner.ConnectionTestUrl;
            set => Stage(KeyConnectionTestUrl, NormalizeConnectionTestUrl(value));
        }

        public string ConnectionTestProxyUrl1
        {
            get => _owner.ConnectionTestProxyUrl1;
            set => Stage(KeyConnectionTestProxyUrl1, NormalizeConnectionTestUrl(value));
        }

        public string ConnectionTestProxyUrl2
        {
            get => _owner.ConnectionTestProxyUrl2;
            set => Stage(KeyConnectionTestProxyUrl2, NormalizeConnectionTestUrl(value));
        }

        public string ConnectionTestDirectUrl
        {
            get => _owner.ConnectionTestDirectUrl;
            set => Stage(KeyConnectionTestDirectUrl, NormalizeConnectionTestUrl(value));
        }

        public string MasterHeroStatusLayout
        {
            get => _owner.MasterHeroStatusLayout;
            set => Stage(KeyMasterHeroStatusLayout, NormalizeMasterHeroStatusLayout(value));
        }

        public string MasterInfoTileLayout
        {
            get => _owner.MasterInfoTileLayout;
            set => Stage(KeyMasterInfoTileLayout, NormalizeMasterInfoTileLayout(value));
        }

        public void ResetAllSettings()
        {
            EnsureActive();
            foreach (string key in KnownKeys)
            {
                _pending[key] = null;
            }
        }

        /// <summary>Stages removal of the exact owned definitions and their legacy aliases.</summary>
        internal void ResetDefinitions(IReadOnlyList<SettingDefinition> definitions)
        {
            EnsureActive();
            foreach (SettingDefinition definition in definitions)
            {
                RemoveOwnedKey(definition.Key.Value);
                foreach (SettingKey alias in definition.Aliases)
                {
                    RemoveOwnedKey(alias.Value);
                }
            }
        }

        private void RemoveOwnedKey(string key)
        {
            if (Array.IndexOf(KnownKeys, key) < 0)
            {
                throw new InvalidOperationException("A preference reset cannot remove an unowned setting.");
            }

            _pending[key] = null;
        }

        internal void ClearAllSettings()
        {
            ResetAllSettings();
        }

        internal IReadOnlyList<AppSettingChangedEventArgs> Commit()
        {
            EnsureActive();
            List<(string Key, object? Previous)> applied = [];
            List<AppSettingChangedEventArgs> changes = [];
            try
            {
                foreach ((string key, object? value) in _pending)
                {
                    object? previous = _owner.GetValue(key);
                    AppSettingChangedEventArgs? change = value is null
                        ? _owner.RemoveValue(key)
                        : _owner.SetValue(key, value);
                    applied.Add((key, previous));
                    if (change is not null)
                    {
                        changes.Add(change);
                    }
                }

                return changes;
            }
            catch (Exception applyFailure)
            {
                Exception? rollbackFailure = null;
                try
                {
                    for (int index = applied.Count - 1; index >= 0; index--)
                    {
                        (string key, object? previous) = applied[index];
                        _ = previous is null
                            ? _owner.RemoveValue(key)
                            : _owner.SetValue(key, previous);
                    }
                }
                catch (Exception exception)
                {
                    rollbackFailure = exception;
                }

                if (rollbackFailure is not null)
                {
                    throw new AggregateException(
                        "A settings batch failed and its in-process rollback was incomplete.",
                        applyFailure,
                        rollbackFailure);
                }

                ExceptionDispatchInfo.Capture(applyFailure).Throw();
                throw;
            }
        }

        internal void Invalidate()
        {
            _active = false;
        }

        private void Stage(string key, object value)
        {
            EnsureActive();
            ArgumentNullException.ThrowIfNull(value);
            _pending[key] = value;
        }

        internal void StageCanonical(SettingValueChange change)
        {
            SettingValue value = change.Value;
            if (change.Key == SettingsRegistry.Keys.MainlandChinaFeatureMode)
            {
                // Retain the legacy alias until the single-authority migration is activated.
                MainlandChinaFeatureMode = value.Get<MainlandChinaFeatureMode>();
                return;
            }
            object stored = value.ValueType == typeof(bool) ? value.Get<bool>()
                : value.ValueType == typeof(int) ? value.Get<int>()
                : value.ValueType == typeof(string) ? value.Get<string>()
                : value.ValueType.IsEnum ? Convert.ToInt32(Enum.Parse(value.ValueType, value.CanonicalText), CultureInfo.InvariantCulture)
                : throw new ArgumentException("The setting type is unsupported.", nameof(change));
            Stage(change.Key.Value, stored);
        }

        private void StageEnum<TEnum>(string key, TEnum value)
            where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The enum value is not defined.");
            }

            Stage(key, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }

        private void EnsureActive()
        {
            ObjectDisposedException.ThrowIf(!_active, this);
        }
    }
}
