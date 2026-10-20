using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class LogsViewModelTests
{
    [Theory]
    [MemberData(nameof(PageLocalizationTestData.Languages), MemberType = typeof(PageLocalizationTestData))]
    public async Task LocalizationRefresh_PreservesRawFiltersRowsAndOriginalLogText(AppLanguage language)
    {
        MutablePageLocalization localization = new() { Language = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English };
        FakeLogManagementStore store = new()
        {
            Sources = ["Core", "Application"],
            Logs = [new(DateTimeOffset.UnixEpoch, "Warning", "Core", "original message", "original details")],
        };
        LogsViewModel page = new(localization.GetString, store, new TestApplicationErrorSink());
        page.SetSourceFilter("Core");
        await page.LoadAsync(CancellationToken.None);
        page.SelectedLevelFilter = page.LevelFilterOptions[2];
        page.ApplySearchText("original");
        await page.LoadAsync(CancellationToken.None);
        LogRecordDisplay row = Assert.Single(page.RecentLogs);
        IReadOnlyList<LogRecordDisplay> rows = page.RecentLogs;
        int reads = store.StorageSummaryReadCount;
        localization.Language = language;

        page.RefreshLocalization();

        Assert.Equal(reads, store.StorageSummaryReadCount);
        Assert.Same(rows, page.RecentLogs);
        Assert.Same(row, Assert.Single(page.RecentLogs));
        Assert.Equal("original message", row.Message);
        Assert.Equal("original details", row.Detail);
        Assert.Equal("original", page.SearchText);
        Assert.Equal(2, page.SelectedLevelFilterIndex);
        Assert.False(page.IsUpdatingFilterOptions);
        await page.LoadAsync(CancellationToken.None);
        Assert.Equal(("Core", "Warning", "original"), store.LastQuery);
    }

    [Fact]
    public async Task LocalizationRefresh_DoesNotConflateTranslatedCategoriesOrBroadenAnOpenCleanupScope()
    {
        string coreLabel = "first";
        string Resolve(string key) => key == "Logs.Source.Core" ? coreLabel : GetString(key);
        (string? Level, string? Source) preview = default;
        FakeLogManagementStore store = new()
        {
            Sources = ["Core", "second"],
            PreviewHandler = (level, source) => { preview = (level, source); return default; },
        };
        LogsViewModel page = new(Resolve, store, new TestApplicationErrorSink());
        page.SetSourceFilter("Core");
        await page.LoadAsync(CancellationToken.None);
        page.SelectedLevelFilter = page.LevelFilterOptions[2];
        LogsViewModel.CleanupFilterSnapshot captured = page.CaptureCleanupFilters();
        coreLabel = "second";

        page.RefreshLocalization();
        await page.GetCleanupPreviewTextAsync(4, 0, captured.SelectedLevel, captured.SelectedCategory, CancellationToken.None, captured);

        Assert.Equal(("Warning", "Core"), preview);
        Assert.Equal(3, page.CategoryFilterOptions.Distinct(StringComparer.Ordinal).Count());
        await page.LoadAsync(CancellationToken.None);
        Assert.Equal("Core", store.LastQuery.Source);
        Assert.Equal("Warning", store.LastQuery.Level);
    }

    [Fact]
    public async Task CapturedCleanupFilter_BindsRequestedSourceBeforeLoadingAndRejectsAnUnknownLabel()
    {
        (string? Level, string? Source) preview = default;
        FakeLogManagementStore store = new()
        {
            PreviewHandler = (level, source) => { preview = (level, source); return default; },
        };
        LogsViewModel page = new(GetString, store, new TestApplicationErrorSink());
        page.SetSourceFilter("Trigger");
        LogsViewModel.CleanupFilterSnapshot captured = page.CaptureCleanupFilters();

        await page.GetCleanupPreviewTextAsync(4, 0, captured.SelectedLevel, captured.SelectedCategory, CancellationToken.None, captured);

        Assert.Equal((null, "Trigger"), preview);
        await Assert.ThrowsAsync<ArgumentException>(() => page.GetCleanupPreviewTextAsync(4, 0,
            "changed label", captured.SelectedCategory, CancellationToken.None, captured));
        Assert.Equal(1, store.PreviewReadCount);
    }
}
