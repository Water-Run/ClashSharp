using System.Globalization;
using System.Text.Json;
using ClashSharp.Settings;

namespace ClashSharp.Infrastructure.Settings;

internal static partial class SettingsEnvelopeCodec
{
    private static byte[] WritePayload(SettingsEnvelope envelope)
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", envelope.SchemaVersion);
            writer.WriteNumber("envelopeRevision", envelope.EnvelopeRevision);
            WriteDesired(writer, envelope);
            WriteApplied(writer, envelope);
            WritePendingApplications(writer, envelope.PendingApplications);
            WriteMigrationHistory(writer, envelope.MigrationHistory);
            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    private static void WriteDesired(
        Utf8JsonWriter writer,
        SettingsEnvelope envelope)
    {
        writer.WriteStartArray("desired");
        foreach ((SettingKey key, SettingDesiredEntry entry) in envelope.Desired
                     .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("key", key.Value);
            writer.WriteString("value", entry.Value.CanonicalText);
            writer.WriteNumber("keyDesiredRevision", entry.KeyDesiredRevision);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteApplied(
        Utf8JsonWriter writer,
        SettingsEnvelope envelope)
    {
        writer.WriteStartArray("applied");
        foreach ((SettingKey key, SettingAppliedState state) in envelope.Applied
                     .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("key", key.Value);
            if (state.Kind == SettingAppliedStateKind.Verified)
            {
                writer.WriteString("kind", "verified");
                writer.WriteString("value", state.Value!.CanonicalText);
                writer.WriteString("source", WriteAppliedSource(state.Source!.Value));
                writer.WriteString("observedHash", state.ObservedHash);
                writer.WriteString(
                    "observedAt",
                    state.ObservedAt!.Value.ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteString("kind", "unknown");
                writer.WriteString(
                    "reason",
                    WriteUnknownReason(state.UnknownReason!.Value));
                writer.WriteString(
                    "handling",
                    WriteUnknownHandling(state.UnknownHandling!.Value));
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WritePendingApplications(
        Utf8JsonWriter writer,
        IReadOnlyList<SettingsApplicationBatch> batches)
    {
        writer.WriteStartArray("pendingApplications");
        foreach (SettingsApplicationBatch batch in batches)
        {
            writer.WriteStartObject();
            writer.WriteString("batchId", batch.BatchId.ToString("D"));
            writer.WriteString("kind", WriteBatchKind(batch.Kind));
            writer.WriteNumber("creationSequence", batch.CreationSequence);
            writer.WriteString("attemptId", batch.AttemptId.ToString("D"));
            writer.WriteString("state", WriteBatchState(batch.State));
            writer.WriteString(
                "applicationKind",
                WriteApplicationKind(batch.ApplicationKind));
            writer.WriteStartArray("entries");
            foreach (SettingsApplicationBatchEntry entry in batch.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("key", entry.Key.Value);
                writer.WriteNumber(
                    "keyDesiredRevision",
                    entry.KeyDesiredRevision);
                writer.WriteString("valueHash", entry.ValueHash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (batch.LastError is null)
            {
                writer.WriteNull("lastError");
            }
            else
            {
                writer.WriteString("lastError", batch.LastError.Code);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMigrationHistory(
        Utf8JsonWriter writer,
        IReadOnlyList<SettingsMigrationRecord> migrations)
    {
        writer.WriteStartArray("migrationHistory");
        foreach (SettingsMigrationRecord migration in migrations)
        {
            writer.WriteStartObject();
            writer.WriteString("migrationId", migration.MigrationId.ToString("D"));
            writer.WriteNumber(
                "fromSchemaVersion",
                migration.FromSchemaVersion);
            writer.WriteNumber("toSchemaVersion", migration.ToSchemaVersion);
            writer.WriteString("sourceHash", migration.SourceHash);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
