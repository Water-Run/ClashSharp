using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

public sealed class SettingsEnvelopeRegionalUpgradeTests
{
    private static readonly string[] NewKeys = ["DataUnitStyle", "RateUnit", "ClockFormat"];

    [Fact]
    public void HistoricalEnvelopeKeepsEveryExistingValueAndQueuesOnlyNewFormats()
    {
        byte[] bytes = VersionOneBytes();
        SettingsEnvelope first = SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default);
        SettingsEnvelope second = SettingsEnvelopeCodec.Decode(bytes, SettingsRegistry.Default);
        Assert.Equal(3, first.SchemaVersion);
        Assert.Equal(1, first.EnvelopeRevision);
        Assert.Equal("Dark", first.Desired[SettingsRegistry.Keys.AppThemeMode].Value.CanonicalText);
        Assert.Equal("BinaryWithDecimalLabels", first.Desired[SettingsRegistry.Keys.DataUnitStyle].Value.CanonicalText);
        Assert.Equal("Bytes", first.Desired[SettingsRegistry.Keys.RateUnit].Value.CanonicalText);
        Assert.Equal("FollowSystem", first.Desired[SettingsRegistry.Keys.ClockFormat].Value.CanonicalText);
        SettingsApplicationBatch batch = Assert.Single(first.PendingApplications, batch => batch.ApplicationKind == SettingApplicationKind.Appearance);
        Assert.Equal(NewKeys.Order(), batch.Entries.Select(entry => entry.Key.Value).Order());
        Assert.All(NewKeys, key => Assert.Equal(SettingAppliedStateKind.Unknown, first.Applied[new(key)].Kind));
        Assert.Equal(2, first.MigrationHistory.Count);
        Assert.False(first.Desired[SettingsRegistry.Keys.McpServerEnabled].Value.Get<bool>());
        Assert.Single(first.PendingApplications, batch => batch.ApplicationKind == SettingApplicationKind.McpServer);
        byte[] upgraded = SettingsEnvelopeCodec.Encode(first, SettingsRegistry.Default).Bytes;
        Assert.Equal(upgraded, SettingsEnvelopeCodec.Encode(second, SettingsRegistry.Default).Bytes);
        Assert.Equal(upgraded, SettingsEnvelopeCodec.Encode(SettingsEnvelopeCodec.Decode(upgraded, SettingsRegistry.Default), SettingsRegistry.Default).Bytes);
    }

    [Fact]
    public void MissingHistoricalPreferenceIsNotSilentlyReplacedByDefaults()
    {
        byte[] damaged = Rewrite(VersionOneBytes(), payload =>
        {
            JsonArray desired = payload["desired"]!.AsArray();
            desired.Remove(desired.Single(item => item!["key"]!.GetValue<string>() == "DisplayLanguage"));
        });
        Assert.Throws<SettingsEnvelopeCodecException>(() => SettingsEnvelopeCodec.Decode(damaged, SettingsRegistry.Default));
    }

    [Fact]
    public void CurrentSchemaWithMissingFormatsIsRejectedInsteadOfTreatedAsAnOldInstall()
    {
        byte[] wrongVersion = Rewrite(VersionOneBytes(), payload => payload["schemaVersion"] = 2);
        Assert.Throws<SettingsEnvelopeCodecException>(() => SettingsEnvelopeCodec.Decode(wrongVersion, SettingsRegistry.Default));
    }

    private static byte[] VersionOneBytes()
    {
        SettingsRegistry historical = SettingsRegistry.Create(SettingsRegistry.Default.Definitions.Where(definition => !NewKeys.Contains(definition.Key.Value)
            && definition.ApplicationKind != SettingApplicationKind.McpServer));
        SettingsEnvelope source = SettingsEnvelopeTestData.CreateMatchingEnvelope(historical);
        SettingValue dark = historical.Get("AppThemeMode").Normalize("Dark").Value!;
        Dictionary<SettingKey, SettingDesiredEntry> desired = source.Desired.ToDictionary();
        Dictionary<SettingKey, SettingAppliedState> applied = source.Applied.ToDictionary();
        desired[SettingsRegistry.Keys.AppThemeMode] = new(dark, 1);
        applied[SettingsRegistry.Keys.AppThemeMode] = SettingAppliedState.Verified(dark, SettingAppliedValueSource.DefaultInitialization,
            SettingsApplicationBatchEntry.ComputeValueHash(dark), SettingsEnvelopeTestData.ObservedAt);
        SettingsEnvelope view = new(SettingsEnvelope.CurrentSchemaVersion, 1, desired, applied, [], []);
        return Rewrite(SettingsEnvelopeCodec.Encode(view, historical).Bytes, payload => payload["schemaVersion"] = 1);
    }

    private static byte[] Rewrite(byte[] bytes, Action<JsonNode> transform)
    {
        using JsonDocument wrapper = JsonDocument.Parse(bytes);
        JsonNode payload = JsonNode.Parse(wrapper.RootElement.GetProperty("payload").GetBytesFromBase64())!;
        transform(payload);
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
