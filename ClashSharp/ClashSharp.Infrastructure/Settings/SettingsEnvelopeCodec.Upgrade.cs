using System.Security.Cryptography;
using System.Text;
using ClashSharp.Settings;

namespace ClashSharp.Infrastructure.Settings;

internal static partial class SettingsEnvelopeCodec
{
    private static readonly SettingKey[] VersionTwoKeys =
    [SettingsRegistry.Keys.DataUnitStyle, SettingsRegistry.Keys.RateUnit, SettingsRegistry.Keys.ClockFormat];
    private static readonly SettingKey[] VersionThreeKeys =
    [SettingsRegistry.Keys.McpServerEnabled, SettingsRegistry.Keys.McpServerPort, SettingsRegistry.Keys.McpAllowedScopes];

    /// <summary>Projects a fully validated historical envelope into the additive display schema.</summary>
    /// <remarks>
    /// The original bytes must still pass canonical round-trip verification. Existing values, revisions,
    /// pending work and evidence are preserved. The next ordinary authority commit durably records the
    /// upgrade; deterministic identities make repeated reads and interrupted startup safe.
    /// </remarks>
    private static SettingsEnvelope UpgradeVersionOne(SettingsEnvelope source, SettingsRegistry registry, string sourceHash)
    {
        if (VersionTwoKeys.Any(key => !registry.Definitions.Any(definition => definition.Key == key))
            || source.MigrationHistory.Any(migration => migration.ToSchemaVersion > 1))
        {
            throw Error("settings.persistence.envelope.schema_unsupported", "$.payload.schemaVersion");
        }
        SettingsRegistry historical = SettingsRegistry.Create(registry.Definitions.Where(definition =>
            !VersionTwoKeys.Contains(definition.Key) && !VersionThreeKeys.Contains(definition.Key)));
        SettingsEnvelope validationView = new(SettingsEnvelope.CurrentSchemaVersion, source.EnvelopeRevision,
            source.Desired, source.Applied, source.PendingApplications, source.MigrationHistory);
        if (!new SettingsEnvelopeValidator(historical).Validate(validationView).IsValid)
        {
            throw Error("settings.persistence.envelope.legacy_invalid", "$.payload");
        }

        Dictionary<SettingKey, SettingDesiredEntry> desired = source.Desired.ToDictionary();
        Dictionary<SettingKey, SettingAppliedState> applied = source.Applied.ToDictionary();
        foreach (SettingKey key in VersionTwoKeys)
        {
            desired.Add(key, new(registry.Get(key.Value).DefaultValue, keyDesiredRevision: 1));
            applied.Add(key, SettingAppliedState.Unknown(SettingAppliedUnknownReason.NotObserved, SettingAppliedUnknownHandling.QueueApplication));
        }
        long sequence = checked(source.PendingApplications.Select(batch => batch.CreationSequence).DefaultIfEmpty(0).Max() + 1);
        SettingsApplicationBatch formatting = new(Identity("batch"), SettingsApplicationBatchKind.LiveReconcile,
            sequence, Identity("attempt"), SettingsApplicationBatchState.Pending, SettingApplicationKind.Appearance,
            VersionTwoKeys.Select(key => SettingsApplicationBatchEntry.Create(key, desired[key])));
        return new(2, source.EnvelopeRevision, desired, applied,
            source.PendingApplications.Append(formatting),
            source.MigrationHistory.Append(new SettingsMigrationRecord(Identity("migration"), 1, 2, sourceHash)));

        Guid Identity(string purpose) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"clashsharp-display-schema-v2/{sourceHash}/{purpose}")).AsSpan(0, 16));
    }

    /// <summary>Adds disabled local-agent configuration without changing any existing preference or application evidence.</summary>
    private static SettingsEnvelope UpgradeVersionTwo(SettingsEnvelope source, SettingsRegistry registry, string sourceHash)
    {
        if (VersionThreeKeys.Any(key => !registry.Definitions.Any(definition => definition.Key == key))
            || source.MigrationHistory.Any(migration => migration.ToSchemaVersion > 2))
        {
            throw Error("settings.persistence.envelope.schema_unsupported", "$.payload.schemaVersion");
        }
        SettingsRegistry historical = SettingsRegistry.Create(registry.Definitions.Where(definition => !VersionThreeKeys.Contains(definition.Key)));
        SettingsEnvelope validationView = new(SettingsEnvelope.CurrentSchemaVersion, source.EnvelopeRevision,
            source.Desired, source.Applied, source.PendingApplications, source.MigrationHistory);
        if (!new SettingsEnvelopeValidator(historical).Validate(validationView).IsValid)
        {
            throw Error("settings.persistence.envelope.legacy_invalid", "$.payload");
        }
        Dictionary<SettingKey, SettingDesiredEntry> desired = source.Desired.ToDictionary();
        Dictionary<SettingKey, SettingAppliedState> applied = source.Applied.ToDictionary();
        foreach (SettingKey key in VersionThreeKeys)
        {
            desired.Add(key, new(registry.Get(key.Value).DefaultValue, keyDesiredRevision: 1));
            applied.Add(key, SettingAppliedState.Unknown(SettingAppliedUnknownReason.NotObserved, SettingAppliedUnknownHandling.QueueApplication));
        }
        long sequence = checked(source.PendingApplications.Select(batch => batch.CreationSequence).DefaultIfEmpty(0).Max() + 1);
        SettingsApplicationBatch agents = new(Identity("batch"), SettingsApplicationBatchKind.LiveReconcile,
            sequence, Identity("attempt"), SettingsApplicationBatchState.Pending, SettingApplicationKind.McpServer,
            VersionThreeKeys.Select(key => SettingsApplicationBatchEntry.Create(key, desired[key])));
        return new(SettingsEnvelope.CurrentSchemaVersion, source.EnvelopeRevision, desired, applied,
            source.PendingApplications.Append(agents),
            source.MigrationHistory.Append(new SettingsMigrationRecord(Identity("migration"), 2, 3, sourceHash)));

        Guid Identity(string purpose) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"clashsharp-agent-schema-v3/{sourceHash}/{purpose}")).AsSpan(0, 16));
    }
}
