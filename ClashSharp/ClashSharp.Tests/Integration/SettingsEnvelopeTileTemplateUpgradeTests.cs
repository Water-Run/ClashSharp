using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies the schema 3 to 4 upgrade that introduces the Master Control layout template list.</summary>
public sealed class SettingsEnvelopeTileTemplateUpgradeTests
{
    private static readonly SettingsRegistry Historical = SettingsRegistry.Create(
        SettingsRegistry.Default.Definitions.Where(static definition => definition.Key != SettingsRegistry.Keys.MasterInfoTilePresets));

    [Fact]
    public void VersionThreeKeepsEveryEntryAndQueuesOnlyTheDefaultTemplateList()
    {
        SettingsEnvelope before = SettingsEnvelopeTestData.CreateMatchingEnvelope(Historical);
        byte[] bytes = WithSchemaVersion(SettingsEnvelopeCodec.Encode(before, Historical).Bytes, 3);

        SettingsEnvelope after = SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default);

        Assert.Equal(4, after.SchemaVersion);
        Assert.Equal(before.EnvelopeRevision, after.EnvelopeRevision);
        Assert.Equal(SettingsRegistry.Default.Definitions.Count, after.Desired.Count);
        foreach (var entry in before.Desired)
        {
            Assert.Equal(entry.Value, after.Desired[entry.Key]);
            Assert.Equal(before.Applied[entry.Key], after.Applied[entry.Key]);
        }

        SettingDesiredEntry templates = after.Desired[SettingsRegistry.Keys.MasterInfoTilePresets];
        Assert.Equal(MasterInfoTilePresetList.DefaultCanonicalText, templates.Value.CanonicalText);
        Assert.Equal(SettingAppliedStateKind.Unknown, after.Applied[SettingsRegistry.Keys.MasterInfoTilePresets].Kind);
        SettingsApplicationBatch batch = Assert.Single(after.PendingApplications);
        Assert.Equal(SettingApplicationKind.Appearance, batch.ApplicationKind);
        Assert.Equal(SettingsApplicationBatchKind.LiveReconcile, batch.Kind);
        Assert.Equal(SettingsRegistry.Keys.MasterInfoTilePresets, Assert.Single(batch.Entries).Key);
        SettingsMigrationRecord migration = Assert.Single(after.MigrationHistory);
        Assert.Equal(3, migration.FromSchemaVersion);
        Assert.Equal(4, migration.ToSchemaVersion);
    }

    [Fact]
    public void UpgradeIsDeterministicAndStableAfterReencoding()
    {
        byte[] bytes = WithSchemaVersion(
            SettingsEnvelopeCodec.Encode(SettingsEnvelopeTestData.CreateMatchingEnvelope(Historical), Historical).Bytes, 3);

        byte[] first = SettingsEnvelopeCodec.Encode(SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default), SettingsRegistry.Default).Bytes;
        byte[] second = SettingsEnvelopeCodec.Encode(SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default), SettingsRegistry.Default).Bytes;

        Assert.Equal(first, second);
        Assert.Equal(first, SettingsEnvelopeCodec.Encode(SettingsEnvelopeCodec.Decode(first, SettingsRegistry.Default), SettingsRegistry.Default).Bytes);
    }

    [Fact]
    public void VersionThreeThatAlreadyContainsTheTemplateListIsRejected()
    {
        SettingsEnvelope current = SettingsEnvelopeTestData.CreateMatchingEnvelope(SettingsRegistry.Default);
        byte[] bytes = WithSchemaVersion(SettingsEnvelopeCodec.Encode(current, SettingsRegistry.Default).Bytes, 3);

        Assert.Throws<SettingsEnvelopeCodecException>(() => SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default));
    }

    [Fact]
    public void CurrentSchemaWithoutTheTemplateListIsRejectedInsteadOfTreatedAsAnOldInstall()
    {
        byte[] bytes = SettingsEnvelopeCodec.Encode(SettingsEnvelopeTestData.CreateMatchingEnvelope(Historical), Historical).Bytes;

        Assert.Throws<SettingsEnvelopeCodecException>(() => SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default));
    }

    private static byte[] WithSchemaVersion(byte[] bytes, int schemaVersion)
    {
        using JsonDocument wrapper = JsonDocument.Parse(bytes);
        JsonNode payload = JsonNode.Parse(wrapper.RootElement.GetProperty("payload").GetBytesFromBase64())!;
        payload["schemaVersion"] = schemaVersion;
        byte[] content = Encoding.UTF8.GetBytes(payload.ToJsonString());
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteBase64String("payload", content);
            writer.WriteString("contentHash", Convert.ToHexStringLower(SHA256.HashData(content)));
            writer.WriteEndObject();
        }
        return output.ToArray();
    }
}
