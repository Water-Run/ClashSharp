using System.Text.Json;
using ClashSharp.Settings;

namespace ClashSharp.Infrastructure.Settings;

internal static partial class SettingsEnvelopeCodec
{
    private static SettingsEnvelope ReadPayload(
        ReadOnlyMemory<byte> payload,
        SettingsRegistry registry)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        IReadOnlyDictionary<string, JsonElement> properties = ReadShape(
            document.RootElement,
            [
                "schemaVersion",
                "envelopeRevision",
                "desired",
                "applied",
                "pendingApplications",
                "migrationHistory",
            ],
            "$.payload");

        return new SettingsEnvelope(
            ReadCanonicalInt32(
                properties["schemaVersion"],
                "$.payload.schemaVersion"),
            ReadCanonicalInt64(
                properties["envelopeRevision"],
                "$.payload.envelopeRevision"),
            ReadDesired(properties["desired"], registry),
            ReadApplied(properties["applied"], registry),
            ReadBatches(properties["pendingApplications"], registry),
            ReadMigrations(properties["migrationHistory"]));
    }

    private static IEnumerable<KeyValuePair<SettingKey, SettingDesiredEntry>>
        ReadDesired(JsonElement element, SettingsRegistry registry)
    {
        List<KeyValuePair<SettingKey, SettingDesiredEntry>> desired = [];
        int index = 0;
        foreach (JsonElement item in ReadArray(element, "$.payload.desired"))
        {
            string path = $"$.payload.desired[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadShape(
                item,
                ["key", "value", "keyDesiredRevision"],
                path);
            SettingDefinition definition =
                ReadDefinition(properties["key"], registry, $"{path}.key");
            desired.Add(new KeyValuePair<SettingKey, SettingDesiredEntry>(
                definition.Key,
                new SettingDesiredEntry(
                    ReadValue(properties["value"], definition, $"{path}.value"),
                    ReadCanonicalInt64(
                        properties["keyDesiredRevision"],
                        $"{path}.keyDesiredRevision"))));
            index++;
        }

        return desired;
    }

    private static IEnumerable<KeyValuePair<SettingKey, SettingAppliedState>>
        ReadApplied(JsonElement element, SettingsRegistry registry)
    {
        List<KeyValuePair<SettingKey, SettingAppliedState>> applied = [];
        int index = 0;
        foreach (JsonElement item in ReadArray(element, "$.payload.applied"))
        {
            string path = $"$.payload.applied[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadObject(
                item,
                [
                    "key",
                    "kind",
                    "value",
                    "source",
                    "observedHash",
                    "observedAt",
                    "reason",
                    "handling",
                ],
                path);
            SettingDefinition definition =
                ReadDefinition(properties.GetValueOrDefault("key"), registry, $"{path}.key");
            string kind = ReadEnumText(
                properties.GetValueOrDefault("kind"),
                $"{path}.kind");
            SettingAppliedState state = kind switch
            {
                "verified" => ReadVerifiedApplied(
                    properties,
                    definition,
                    path),
                "unknown" => ReadUnknownApplied(properties, path),
                _ => throw EnumError($"{path}.kind"),
            };
            applied.Add(
                new KeyValuePair<SettingKey, SettingAppliedState>(
                    definition.Key,
                    state));
            index++;
        }

        return applied;
    }

    private static SettingAppliedState ReadVerifiedApplied(
        IReadOnlyDictionary<string, JsonElement> properties,
        SettingDefinition definition,
        string path)
    {
        RequireShape(
            properties,
            ["key", "kind", "value", "source", "observedHash", "observedAt"],
            path);
        return SettingAppliedState.Verified(
            ReadValue(properties["value"], definition, $"{path}.value"),
            ReadAppliedSource(properties["source"], $"{path}.source"),
            ReadCanonicalHash(
                properties["observedHash"],
                $"{path}.observedHash"),
            ReadCanonicalUtcTimestamp(
                properties["observedAt"],
                $"{path}.observedAt"));
    }

    private static SettingAppliedState ReadUnknownApplied(
        IReadOnlyDictionary<string, JsonElement> properties,
        string path)
    {
        RequireShape(properties, ["key", "kind", "reason", "handling"], path);
        return SettingAppliedState.Unknown(
            ReadUnknownReason(properties["reason"], $"{path}.reason"),
            ReadUnknownHandling(properties["handling"], $"{path}.handling"));
    }

    private static IEnumerable<SettingsApplicationBatch> ReadBatches(
        JsonElement element,
        SettingsRegistry registry)
    {
        List<SettingsApplicationBatch> batches = [];
        int index = 0;
        foreach (JsonElement item in ReadArray(
                     element,
                     "$.payload.pendingApplications"))
        {
            string path = $"$.payload.pendingApplications[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadShape(
                item,
                [
                    "batchId",
                    "kind",
                    "creationSequence",
                    "attemptId",
                    "state",
                    "applicationKind",
                    "entries",
                    "lastError",
                ],
                path);
            SettingsApplicationBatchState state =
                ReadBatchState(properties["state"], $"{path}.state");
            batches.Add(new SettingsApplicationBatch(
                ReadCanonicalGuid(properties["batchId"], $"{path}.batchId"),
                ReadBatchKind(properties["kind"], $"{path}.kind"),
                ReadCanonicalInt64(
                    properties["creationSequence"],
                    $"{path}.creationSequence"),
                ReadCanonicalGuid(properties["attemptId"], $"{path}.attemptId"),
                state,
                ReadApplicationKind(
                    properties["applicationKind"],
                    $"{path}.applicationKind"),
                ReadBatchEntries(properties["entries"], registry, path),
                ReadLastError(properties["lastError"], state, path)));
            index++;
        }

        return batches;
    }

    private static IEnumerable<SettingsApplicationBatchEntry> ReadBatchEntries(
        JsonElement element,
        SettingsRegistry registry,
        string batchPath)
    {
        List<SettingsApplicationBatchEntry> entries = [];
        int index = 0;
        foreach (JsonElement item in ReadArray(element, $"{batchPath}.entries"))
        {
            string path = $"{batchPath}.entries[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadShape(
                item,
                ["key", "keyDesiredRevision", "valueHash"],
                path);
            SettingDefinition definition =
                ReadDefinition(properties["key"], registry, $"{path}.key");
            entries.Add(new SettingsApplicationBatchEntry(
                definition.Key,
                ReadCanonicalInt64(
                    properties["keyDesiredRevision"],
                    $"{path}.keyDesiredRevision"),
                ReadCanonicalHash(
                    properties["valueHash"],
                    $"{path}.valueHash")));
            index++;
        }

        return entries;
    }

    private static SettingsApplicationError? ReadLastError(
        JsonElement element,
        SettingsApplicationBatchState state,
        string batchPath)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string code = ReadString(element, $"{batchPath}.lastError");
        return new SettingsApplicationError(code);
    }

    private static IEnumerable<SettingsMigrationRecord> ReadMigrations(
        JsonElement element)
    {
        List<SettingsMigrationRecord> migrations = [];
        int index = 0;
        foreach (JsonElement item in ReadArray(
                     element,
                     "$.payload.migrationHistory"))
        {
            string path = $"$.payload.migrationHistory[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadShape(
                item,
                [
                    "migrationId",
                    "fromSchemaVersion",
                    "toSchemaVersion",
                    "sourceHash",
                ],
                path);
            migrations.Add(new SettingsMigrationRecord(
                ReadCanonicalGuid(
                    properties["migrationId"],
                    $"{path}.migrationId"),
                ReadCanonicalInt32(
                    properties["fromSchemaVersion"],
                    $"{path}.fromSchemaVersion"),
                ReadCanonicalInt32(
                    properties["toSchemaVersion"],
                    $"{path}.toSchemaVersion"),
                ReadCanonicalHash(
                    properties["sourceHash"],
                    $"{path}.sourceHash")));
            index++;
        }

        return migrations;
    }
}
