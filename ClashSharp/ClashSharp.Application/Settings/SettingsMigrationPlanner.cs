using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Explains a migration decision using only an allowlisted key and stable code.</summary>
/// <param name="Key">Canonical preference key.</param>
/// <param name="Code">Stable decision code; source values are never included.</param>
public sealed record SettingsMigrationDiagnostic(SettingKey Key, string Code);

/// <summary>Contains the complete initial envelope and value-free migration decisions.</summary>
/// <param name="Envelope">Validated canonical envelope with pending application coverage.</param>
/// <param name="Diagnostics">Immutable key-addressed migration decisions.</param>
public sealed record SettingsMigrationPlan(SettingsEnvelope Envelope, IReadOnlyList<SettingsMigrationDiagnostic> Diagnostics);

/// <summary>Converts legacy preferences into a complete canonical authority without asserting runtime effects.</summary>
public sealed class SettingsMigrationPlanner
{
    private readonly SettingsRegistry _registry;

    /// <summary>Creates a pure planner for the canonical schema.</summary>
    /// <param name="registry">Immutable preference definitions and validation rules.</param>
    public SettingsMigrationPlanner(SettingsRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Creates one revision-one migration with deterministic application identities.</summary>
    /// <param name="snapshot">Immutable allowlisted source.</param>
    /// <param name="migrationId">Stable caller-owned identity for this migration attempt.</param>
    public SettingsMigrationPlan CreatePlan(LegacySettingsSnapshot snapshot, Guid migrationId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (migrationId == Guid.Empty)
        {
            throw new ArgumentException("Migration identity cannot be empty.", nameof(migrationId));
        }

        Dictionary<SettingKey, SettingDesiredEntry> desired = [];
        Dictionary<SettingKey, SettingAppliedState> applied = [];
        List<SettingsMigrationDiagnostic> diagnostics = [];
        foreach (SettingDefinition definition in _registry.Definitions)
        {
            (SettingValue value, string code) = ReadValue(snapshot, definition);
            desired.Add(definition.Key, new SettingDesiredEntry(value, keyDesiredRevision: 1));
            applied.Add(definition.Key, SettingAppliedState.Unknown(
                SettingAppliedUnknownReason.NotObserved, SettingAppliedUnknownHandling.QueueApplication));
            diagnostics.Add(new(definition.Key, code));
        }

        List<SettingsApplicationBatch> pending = [];
        foreach (var group in _registry.Definitions
                     .GroupBy(definition => (definition.ApplicationKind, definition.ApplicationTiming))
                     .OrderBy(group => group.Key.ApplicationTiming)
                     .ThenBy(group => group.Key.ApplicationKind))
        {
            long sequence = pending.Count + 1;
            pending.Add(new SettingsApplicationBatch(
                CreateIdentity(migrationId, sequence, "batch"),
                group.Key.ApplicationTiming == SettingApplicationTiming.Restart
                    ? SettingsApplicationBatchKind.Restart
                    : SettingsApplicationBatchKind.LiveReconcile,
                sequence,
                CreateIdentity(migrationId, sequence, "attempt"),
                SettingsApplicationBatchState.Pending,
                group.Key.ApplicationKind,
                group.Select(definition => SettingsApplicationBatchEntry.Create(definition.Key, desired[definition.Key]))));
        }

        SettingsEnvelope envelope = new(SettingsEnvelope.CurrentSchemaVersion, 1, desired, applied, pending,
            [new SettingsMigrationRecord(migrationId, 0, SettingsEnvelope.CurrentSchemaVersion, snapshot.SourceHash)]);
        SettingsEnvelopeValidationResult validation = new SettingsEnvelopeValidator(_registry).Validate(envelope);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("The generated migration envelope violates the canonical schema.");
        }

        return new(envelope, new ReadOnlyCollection<SettingsMigrationDiagnostic>(diagnostics));
    }

    private static (SettingValue Value, string Code) ReadValue(LegacySettingsSnapshot source, SettingDefinition definition)
    {
        string key = definition.Key.Value;
        bool present = source.TryGetValue(key, out object? raw);
        string code = "settings.migration.canonical";
        if (!present)
        {
            foreach (SettingKey aliasKey in definition.Aliases)
            {
                if (source.TryGetValue(aliasKey.Value, out raw))
                {
                    present = true;
                    code = "settings.migration.alias";
                    break;
                }
            }
        }

        if (key == SettingsRegistry.Keys.MainlandChinaFeatureMode.Value)
        {
            if (raw is int legacyMode && legacyMode == (int)MainlandChinaFeatureMode.AllIncludingUrlBlacklist)
            {
                raw = (int)MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;
                code = "settings.migration.legacy_mode_split";
            }
            else if ((!present || raw is not int || !Enum.IsDefined(typeof(MainlandChinaFeatureMode), raw))
                     && source.TryGetValue(SettingsRegistry.Keys.MainlandChinaDisplayEnabled.Value, out object? alias)
                     && alias is bool display)
            {
                raw = (int)(display ? MainlandChinaFeatureMode.FlagReplacementAndTextCompletion : MainlandChinaFeatureMode.Disabled);
                present = true;
                code = "settings.migration.alias";
            }
        }
        else if (key == SettingsRegistry.Keys.MainlandChinaUrlBlockingEnabled.Value
                 && source.TryGetValue(SettingsRegistry.Keys.MainlandChinaFeatureMode.Value, out object? mode)
                 && mode is int oldMode && oldMode == (int)MainlandChinaFeatureMode.AllIncludingUrlBlacklist)
        {
            raw = true;
            present = true;
            code = "settings.migration.legacy_mode_split";
        }

        if (!present)
        {
            return (definition.DefaultValue, "settings.migration.default");
        }

        SettingNormalizationResult normalized = definition.ValueType.IsEnum && raw is int enumValue
            ? definition.NormalizeValue(Enum.ToObject(definition.ValueType, enumValue))
            : definition.NormalizeValue(raw);
        return normalized.IsSuccess
            ? (normalized.Value!, code)
            : (definition.SafeFallback, "settings.migration.invalid_fallback");
    }

    private static Guid CreateIdentity(Guid migrationId, long sequence, string purpose)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"clashsharp-settings-migration-v1/{migrationId:N}/{sequence}/{purpose}")));
        return new Guid(hash.AsSpan(0, 16));
    }
}
