using System.Globalization;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class LogsViewModelTests
{
    [Fact]
    public async Task CleanupSnapshot_RelocalizesAnObservedCountWithoutReadingNewRows()
    {
        bool translated = false;
        long count = 17;
        int reads = 0;
        string Resolve(string key) => key == "Logs.Cleanup.Preview.Count"
            ? translated ? "new {0:N0}" : "old {0:N0}" : GetString(key);
        FakeLogManagementStore store = new() { PreviewAllHandler = () => { reads++; return count; } };
        LogsViewModel page = new(Resolve, store, new TestApplicationErrorSink());
        LogsViewModel.CleanupPreview preview = Assert.IsType<LogsViewModel.CleanupPreview>(
            await page.GetCleanupPreviewAsync(3, 0, null, null, CancellationToken.None));
        Assert.Equal("old 17", page.FormatCleanupPreview(preview));

        translated = true;
        count = 901;
        page.RefreshLocalization();

        Assert.Equal("new 17", page.FormatCleanupPreview(preview));
        Assert.Equal("new 17", page.FormatCleanupPreview(preview));
        Assert.Equal(1, reads);
        Assert.Equal(17, preview.EntryCount);
    }

    [Fact]
    public async Task CleanupSnapshot_PreservesCapturedCapacityScaleAcrossUnitChanges()
    {
        DisplayFormattingOptions options = new(DataUnitStyle.Binary);
        DisplayFormatter formatter = new(() => options, () => CultureInfo.InvariantCulture);
        FakeLogManagementStore store = new() { GetStorageSummaryHandler = () => new(8 * 1024 * 1024, 1, 0) };
        LogsViewModel page = new(GetString, store, new TestApplicationErrorSink(), formatter: formatter);
        LogsViewModel.CleanupFilterSnapshot filters = page.CaptureCleanupFilters();
        options = options with { DataUnitStyle = DataUnitStyle.Si };

        LogsViewModel.CleanupPreview preview = Assert.IsType<LogsViewModel.CleanupPreview>(
            await page.GetCleanupPreviewAsync(1, 3, filters.SelectedLevel, filters.SelectedCategory, CancellationToken.None, filters));
        string text = page.FormatCleanupPreview(preview);
        int reads = store.StorageSummaryReadCount;
        Assert.Contains("3.00 MiB", text, StringComparison.Ordinal);
        Assert.Equal("MiB", filters.Formatter.MegaUnitLabel);

        options = options with { DataUnitStyle = DataUnitStyle.BinaryWithDecimalLabels };
        Assert.Equal(text, page.FormatCleanupPreview(preview));
        Assert.Equal(reads, store.StorageSummaryReadCount);
        await page.ApplyCleanupModeAsync(preview, CancellationToken.None);
        Assert.Equal(3 * 1024L * 1024L, store.LastCleanupSize);
    }

    [Fact]
    public async Task CleanupSnapshot_UsesOriginalCategoryIdentityAfterTranslatedLabelCollisions()
    {
        string coreLabel = "first";
        string Resolve(string key) => key == "Logs.Source.Core" ? coreLabel : GetString(key);
        FakeLogManagementStore store = new() { Sources = ["Core", "second"] };
        LogsViewModel page = new(Resolve, store, new TestApplicationErrorSink());
        page.SetSourceFilter("Core");
        await page.LoadAsync(CancellationToken.None);
        page.SelectedLevelFilter = page.LevelFilterOptions[2];
        LogsViewModel.CleanupFilterSnapshot filters = page.CaptureCleanupFilters();
        LogsViewModel.CleanupPreview preview = Assert.IsType<LogsViewModel.CleanupPreview>(
            await page.GetCleanupPreviewAsync(4, 0, filters.SelectedLevel, filters.SelectedCategory, CancellationToken.None, filters));
        int reads = store.PreviewReadCount;

        coreLabel = "second";
        page.RefreshLocalization();
        IReadOnlyDictionary<string, string> labels = page.GetCleanupFilterLabels(filters, levels: false);

        Assert.Equal(labels.Count, labels.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("second (Core)", labels[filters.SelectedCategory]);
        Assert.Equal(reads, store.PreviewReadCount);
        await page.ApplyCleanupModeAsync(preview, CancellationToken.None);
        Assert.Equal(("Warning", "Core"), store.LastCleanupFilter);
    }

    [Fact]
    public async Task CleanupSnapshot_RejectsForeignAndRepeatedConsumption()
    {
        int cleanups = 0;
        FakeLogManagementStore store = new() { ClearAllHandler = () => cleanups++ };
        LogsViewModel page = CreateViewModel(store, new TestApplicationErrorSink());
        LogsViewModel other = CreateViewModel(store, new TestApplicationErrorSink());
        LogsViewModel.CleanupPreview preview = Assert.IsType<LogsViewModel.CleanupPreview>(
            await page.GetCleanupPreviewAsync(3, 0, null, null, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => other.ApplyCleanupModeAsync(preview, CancellationToken.None));
        Assert.Equal(0, cleanups);
        await page.ApplyCleanupModeAsync(preview, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => page.ApplyCleanupModeAsync(preview, CancellationToken.None));
        Assert.Equal(1, cleanups);
    }

    [Fact]
    public async Task CleanupSnapshot_CancelledAcceptanceDoesNotConsumeOrMutate()
    {
        int cleanups = 0;
        FakeLogManagementStore store = new() { ClearAllHandler = () => cleanups++ };
        LogsViewModel page = CreateViewModel(store, new TestApplicationErrorSink());
        LogsViewModel.CleanupPreview preview = Assert.IsType<LogsViewModel.CleanupPreview>(
            await page.GetCleanupPreviewAsync(3, 0, null, null, CancellationToken.None));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => page.ApplyCleanupModeAsync(preview, cancelled.Token));
        Assert.Equal(0, cleanups);
        await page.ApplyCleanupModeAsync(preview, CancellationToken.None);
        Assert.Equal(1, cleanups);
    }
}
