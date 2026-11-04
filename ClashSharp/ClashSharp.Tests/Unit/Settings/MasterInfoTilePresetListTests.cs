using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies the persisted Master Control layout template list format.</summary>
public sealed class MasterInfoTilePresetListTests
{
    [Fact]
    public void DefaultListReferencesEveryBuiltInInOrder()
    {
        Assert.True(MasterInfoTilePresetList.TryParse(MasterInfoTilePresetList.DefaultCanonicalText, out IReadOnlyList<MasterInfoTilePresetEntry> entries, out _));

        Assert.Equal(MasterInfoTilePresetList.BuiltInIds, entries.Select(static entry => entry.BuiltInId));
        Assert.All(entries, static entry => Assert.Empty(entry.TileIds));
    }

    [Fact]
    public void CustomNamesWithDelimitersRoundTrip()
    {
        MasterInfoTilePresetEntry custom = MasterInfoTilePresetEntry.Custom("  我的;布局: A,B  ", ["Latency", "core", "latency"]);
        string canonical = MasterInfoTilePresetList.Format([MasterInfoTilePresetEntry.BuiltIn("daily"), custom]);

        Assert.True(MasterInfoTilePresetList.TryParse(canonical, out IReadOnlyList<MasterInfoTilePresetEntry> entries, out _));
        MasterInfoTilePresetEntry parsed = entries[1];
        Assert.Equal("我的;布局: A,B", parsed.Name);
        Assert.Equal(["latency", "core"], parsed.TileIds);
        Assert.Equal(canonical, MasterInfoTilePresetList.Format(entries));
    }

    [Fact]
    public void DuplicatesKeepTheFirstEntryAndUnknownBuiltInsArePreserved()
    {
        string text = "@daily;@future-layout;@daily;custom:Work:core;custom:WORK:latency";

        Assert.True(MasterInfoTilePresetList.TryParse(text, out IReadOnlyList<MasterInfoTilePresetEntry> entries, out _));

        Assert.Equal(3, entries.Count);
        Assert.Equal("future-layout", entries[1].BuiltInId);
        Assert.Equal(["core"], entries[2].TileIds);
        Assert.Equal("@daily;@future-layout;custom:Work:core", MasterInfoTilePresetList.Format(entries));
    }

    [Theory]
    [InlineData("@../daily", "master_info_tile_preset.unsafe_id")]
    [InlineData("custom::core", "master_info_tile_preset.invalid_custom")]
    [InlineData("custom:Name:", "master_info_tile_preset.invalid_custom")]
    [InlineData("custom:Name:core,../x", "master_info_tile_preset.invalid_custom")]
    [InlineData("daily", "master_info_tile_preset.unknown_entry")]
    public void InvalidEntriesAreRejected(string text, string expectedError)
    {
        Assert.False(MasterInfoTilePresetList.TryParse(text, out _, out string? error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void ListLengthAndNameLengthAreBounded()
    {
        string tooMany = string.Join(';', Enumerable.Range(0, MasterInfoTilePresetList.MaximumEntries + 1).Select(static index => $"custom:T{index}:core"));
        Assert.False(MasterInfoTilePresetList.TryParse(tooMany, out _, out string? error));
        Assert.Equal("master_info_tile_preset.too_many", error);

        Assert.False(MasterInfoTilePresetList.TryNormalizeName(new string('x', MasterInfoTilePresetList.MaximumNameLength + 1), out _));
        Assert.False(MasterInfoTilePresetList.TryNormalizeName("bell\u0007name", out _));
        Assert.False(MasterInfoTilePresetList.TryNormalizeName("   ", out _));
        Assert.True(MasterInfoTilePresetList.TryNormalizeName(" 日常  使用 ", out string normalized));
        Assert.Equal("日常 使用", normalized);
    }

    [Fact]
    public void RegistryNormalizesAndRejectsThroughTheSameRules()
    {
        SettingDefinition definition = SettingsRegistry.Default.Get(SettingsRegistry.Keys.MasterInfoTilePresets.Value);

        Assert.Equal(MasterInfoTilePresetList.DefaultCanonicalText, definition.DefaultValue.CanonicalText);
        Assert.Equal("@daily;custom:A:core", definition.Normalize(" @daily ; custom:A:CORE ; @daily ").Value!.CanonicalText);
        Assert.False(definition.Normalize("@bad id").IsSuccess);
        Assert.True(definition.Normalize(string.Empty).IsSuccess);
    }
}
