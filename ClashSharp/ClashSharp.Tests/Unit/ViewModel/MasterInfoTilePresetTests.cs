using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Theory]
    [InlineData("daily")]
    [InlineData("diagnostics")]
    [InlineData("subscriptions")]
    [InlineData("minimal")]
    public async Task TilePresets_SaveEveryDefinedTileAndRestoreThePresetOrder(string id)
    {
        FakeMasterInfoTileLayoutService store = new() { SavedLayout = ["latency", "core"] };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        IReadOnlyList<MasterInfoTilePreset> presets = MasterInfoTilePresetCatalog.Create(static key => key, viewModel.RecommendedInfoTileIds);
        Assert.Equal(4, presets.Select(static preset => preset.Id).Distinct(StringComparer.Ordinal).Count());
        MasterInfoTilePreset preset = Assert.Single(presets, preset => preset.Id == id);
        Assert.NotEmpty(preset.TileIds);
        Assert.Equal(preset.TileIds.Count, preset.TileIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(preset.TileIds, tileId => Assert.Contains(viewModel.InfoTiles, tile => tile.Id == tileId));
        Assert.Equal(["latency", "core"], store.SavedLayout);
        Assert.Equal(0, store.SaveCount);

        await viewModel.SetVisibleInfoTileIdsAsync(preset.TileIds, CancellationToken.None);
        MasterControlViewModel reopened = CreateViewModel(infoTileLayout: store);
        await reopened.LoadAsync(CancellationToken.None);

        Assert.Equal(preset.TileIds, store.SavedLayout);
        Assert.Equal(preset.TileIds, reopened.VisibleInfoTiles.Select(static tile => tile.Id));
    }
}
