using System.Globalization;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class StatisticsRefreshViewModelTests
{
    [Fact]
    public async Task LocalizationRefresh_DoesNotPublishAStatisticsSnapshotThatFailedPresentation()
    {
        MutablePageLocalization localization = new();
        StatisticsStore store = WithRows();
        bool fail = false;
        StatisticsViewModel page = new(localization, store, new Profiles(), () => { }, new TestApplicationErrorSink(),
            new ModelDisplayMapper(text => fail ? throw new InvalidOperationException("display unavailable") : text));
        await page.LoadAsync(CancellationToken.None);
        store.ReadSummary = () => Summary(99);
        fail = true;
        await page.LoadAsync(CancellationToken.None);
        Assert.True(page.HasLoadError);
        int reads = store.ReadCount;
        fail = false;
        localization.Language = AppLanguage.German;

        page.RefreshLocalization();

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("Statistics.ConnectionCount.Format"), 7), page.ConnectionCountText);
        Assert.True(page.HasLoadError);
        Assert.Equal(reads, store.ReadCount);
    }
}
