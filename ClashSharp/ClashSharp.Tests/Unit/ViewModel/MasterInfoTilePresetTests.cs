using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Theory]
    [InlineData("default", 12)]
    [InlineData("extended", 24)]
    [InlineData("minimal", 6)]
    [InlineData("technical", 21)]
    [InlineData("general", 12)]
    [InlineData("complete", null)]
    public async Task TilePresets_SaveEveryDefinedTileAndRestoreThePresetOrder(string id, int? tileCount)
    {
        FakeMasterInfoTileLayoutService store = new() { SavedLayout = ["latency", "core"] };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        string[] availableTileIds = viewModel.InfoTiles.Select(static tile => tile.Id).ToArray();
        IReadOnlyList<MasterInfoTilePreset> presets = MasterInfoTilePresetCatalog.Create(
            static key => key, viewModel.RecommendedInfoTileIds, availableTileIds, viewModel.InfoTilePresetEntries);
        Assert.Equal(MasterInfoTilePresetList.BuiltInIds, presets.Select(static preset => preset.Id));
        MasterInfoTilePreset preset = Assert.Single(presets, preset => preset.Id == id);
        Assert.Equal(tileCount ?? availableTileIds.Length, preset.TileIds.Count);
        Assert.Equal(preset.TileIds.Count, preset.TileIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(preset.TileIds, tileId => Assert.Contains(tileId, availableTileIds));
        if (id == "complete")
        {
            Assert.Equal(availableTileIds, preset.TileIds);
        }
        if (id == "default")
        {
            Assert.Equal(viewModel.RecommendedInfoTileIds, preset.TileIds);
        }
        Assert.Equal(["latency", "core"], store.SavedLayout);
        Assert.Equal(0, store.SaveCount);

        await viewModel.SetVisibleInfoTileIdsAsync(preset.TileIds, CancellationToken.None);
        MasterControlViewModel reopened = CreateViewModel(infoTileLayout: store);
        await reopened.LoadAsync(CancellationToken.None);

        Assert.Equal(preset.TileIds, store.SavedLayout);
        Assert.Equal(preset.TileIds, reopened.VisibleInfoTiles.Select(static tile => tile.Id));
    }

    [Fact]
    public void TilePresetCatalog_ResolvesSavedTemplatesInOrderAndSkipsUnknownBuiltIns()
    {
        Assert.True(MasterInfoTilePresetList.TryParse("custom:Work:latency,core;@future-layout;@minimal;@daily;@traffic;@subscriptions;@diagnostics", out IReadOnlyList<MasterInfoTilePresetEntry> entries, out _));

        IReadOnlyList<MasterInfoTilePreset> presets = MasterInfoTilePresetCatalog.Create(
            static key => key == "Master.Preset.CustomTemplate.Format" ? "{0} tiles" : key, ["core"], ["core", "latency"], entries);

        Assert.Equal(["custom:Work", "minimal", "daily", "traffic", "subscriptions", "diagnostics"], presets.Select(static preset => preset.Id));
        MasterInfoTilePreset custom = presets[0];
        Assert.False(custom.IsBuiltIn);
        Assert.Equal("Work", custom.Title);
        Assert.Equal("2 tiles", custom.Description);
        Assert.Equal(["latency", "core"], custom.TileIds);
        Assert.True(presets[1].IsBuiltIn);
        Assert.Equal(["core"], presets[2].TileIds);
    }
}
