using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

public sealed class SettingsEnvelopeAgentUpgradeTests
{
    [Fact]
    public void VersionTwoKeepsAllExistingEntriesAndAddsOnlyDisabledLocalAgentDefaults()
    {
        SettingsRegistry historical = SettingsRegistry.Create(SettingsRegistry.Default.Definitions.Where(definition => definition.ApplicationKind != SettingApplicationKind.McpServer));
        SettingsEnvelope before = SettingsEnvelopeTestData.CreateMatchingEnvelope(historical);
        byte[] bytes = VersionTwo(SettingsEnvelopeCodec.Encode(before, historical).Bytes);
        SettingsEnvelope after = SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default);
        Assert.Equal(3, after.SchemaVersion);
        Assert.Equal(37, after.Desired.Count);
        foreach (var entry in before.Desired)
        {
            Assert.Equal(entry.Value, after.Desired[entry.Key]);
            Assert.Equal(before.Applied[entry.Key], after.Applied[entry.Key]);
        }
        Assert.False(after.Desired[SettingsRegistry.Keys.McpServerEnabled].Value.Get<bool>());
        Assert.Equal(1, after.Desired[SettingsRegistry.Keys.McpAllowedScopes].Value.Get<int>());
        SettingsApplicationBatch batch = Assert.Single(after.PendingApplications);
        Assert.Equal(SettingApplicationKind.McpServer, batch.ApplicationKind);
        Assert.Equal(3, batch.Entries.Count);
        Assert.Equal(2, Assert.Single(after.MigrationHistory).FromSchemaVersion);
        Assert.Equal(SettingsEnvelopeCodec.Encode(after, SettingsRegistry.Default).Bytes,
            SettingsEnvelopeCodec.Encode(SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default), SettingsRegistry.Default).Bytes);
    }

    [Fact]
    public void LegacyPreferenceInputCannotEnableAgentOrImportGrants()
    {
        LegacySettingsSnapshot input = new(SettingsRegistry.Default, new Dictionary<string, object?>
        {
            ["McpServerEnabled"] = true,
            ["McpServerPort"] = 32000,
            ["McpAllowedScopes"] = 255,
        });
        var result = new SettingsMigrationPlanner(SettingsRegistry.Default).CreatePlan(input, Guid.NewGuid());
        Assert.False(result.Envelope.Desired[SettingsRegistry.Keys.McpServerEnabled].Value.Get<bool>());
        Assert.Equal(17892, result.Envelope.Desired[SettingsRegistry.Keys.McpServerPort].Value.Get<int>());
        Assert.Equal(1, result.Envelope.Desired[SettingsRegistry.Keys.McpAllowedScopes].Value.Get<int>());
        Assert.All(SettingsRegistry.Default.Definitions.Where(definition => definition.ApplicationKind == SettingApplicationKind.McpServer),
            definition => Assert.False(definition.IncludeInDataPackage));
    }

    private static byte[] VersionTwo(byte[] bytes)
    {
        using JsonDocument wrapper = JsonDocument.Parse(bytes);
        JsonNode payload = JsonNode.Parse(wrapper.RootElement.GetProperty("payload").GetBytesFromBase64())!;
        payload["schemaVersion"] = 2;
        byte[] content = Encoding.UTF8.GetBytes(payload.ToJsonString());
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject(); writer.WriteNumber("formatVersion", 1); writer.WriteBase64String("payload", content);
            writer.WriteString("contentHash", Convert.ToHexStringLower(SHA256.HashData(content))); writer.WriteEndObject();
        }
        return output.ToArray();
    }
}
