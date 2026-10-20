using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Theory]
    [MemberData(nameof(PageLocalizationTestData.Languages), MemberType = typeof(PageLocalizationTestData))]
    public async Task LocalizationRefresh_PreservesTilesSelectionHistoryAndVerifiedRuntime(AppLanguage language)
    {
        MutablePageLocalization localization = new() { Language = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FakeMasterCore core = new();
        FakeMasterRuntime runtime = new() { Snapshot = ObservedNetwork(ClashSharpMode.Standby) };
        MasterControlViewModel page = CreateViewModel(core: core, runtime: runtime, localization: localization, getNow: () => now);
        await page.LoadAsync(CancellationToken.None);
        MasterControlInfoTileViewModel tile = page.InfoTiles.Single(item => item.Id == "upload-rate");
        double[] history = tile.History;
        MasterHeroStatusSlotViewModel slot = page.HeroStatusSlots[0];
        MasterHeroStatusOptionViewModel option = slot.Options[0];
        MasterHeroStatusItemKind selected = slot.SelectedKind;
        string displayedMode = page.RuleTakeoverModeTitleText;
        page.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(page.RuleTakeoverModeTitleText)) { displayedMode = page.RuleTakeoverModeTitleText; }
        };
        int probes = core.VersionProbeCount;
        int observations = runtime.SnapshotCount;
        now = now.AddMinutes(1);
        localization.Language = language;

        page.RefreshLocalization();

        Assert.Equal(localization.GetString("Master.Mode.RuleTakeover.Title"), displayedMode);
        Assert.Equal(localization.GetString("Master.Tile.UploadRate"), tile.Title);
        Assert.Equal(localization.GetString($"Master.Hero.Item.{option.Kind}"), option.Title);
        Assert.Same(slot, page.HeroStatusSlots[0]);
        Assert.Same(option, slot.Options[0]);
        Assert.Same(tile, page.InfoTiles.Single(item => item.Id == "upload-rate"));
        Assert.Same(history, tile.History);
        Assert.Equal(selected, slot.SelectedKind);
        Assert.Equal(ClashSharpMode.Standby, page.SelectedMode);
        Assert.Equal(observations, runtime.SnapshotCount);
        Assert.Equal(probes, core.VersionProbeCount);
    }
}
