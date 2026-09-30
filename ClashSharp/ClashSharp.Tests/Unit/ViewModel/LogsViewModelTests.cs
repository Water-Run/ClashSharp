using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ServiceProtocol;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Verifies asynchronous log maintenance and preview presentation boundaries.</summary>
public sealed partial class LogsViewModelTests
{
    [Fact]
    public async Task LoadAsync_EmptyRequestedCategorySurvivesNativeItemChangesAndRefresh()
    {
        FakeLogManagementStore store = new() { Sources = ["Application"] };
        LogsViewModel viewModel = CreateViewModel(store, new TestApplicationErrorSink());
        viewModel.SetSourceFilter("Trigger");
        int visibleIndex = -1;
        ((INotifyCollectionChanged)viewModel.CategoryFilterOptions).CollectionChanged += (_, _) =>
        {
            Assert.True(viewModel.IsUpdatingFilterOptions);
            visibleIndex = -1;
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.SelectedCategoryFilterIndex))
            {
                Assert.False(viewModel.IsUpdatingFilterOptions);
                visibleIndex = viewModel.SelectedCategoryFilterIndex;
            }
        };

        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Nav.Triggers", viewModel.CategoryFilterOptions[visibleIndex]);
        store.Sources = ["Application", "Notifications"];
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Nav.Triggers", viewModel.CategoryFilterOptions[visibleIndex]);
        Assert.Equal("Trigger", store.LastQuery.Source);
        Assert.Empty(viewModel.RecentLogs);
    }

    [Fact]
    public async Task LoadAsync_RetainsFiltersAcrossRefreshAndNewCategories()
    {
        FakeLogManagementStore store = new() { Sources = ["Application"] };
        LogsViewModel viewModel = CreateViewModel(store, new TestApplicationErrorSink());
        Assert.Equal(-1, viewModel.SelectedLevelFilterIndex);
        Assert.Equal(-1, viewModel.SelectedCategoryFilterIndex);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal(0, viewModel.SelectedLevelFilterIndex);
        Assert.Equal(0, viewModel.SelectedCategoryFilterIndex);
        viewModel.SelectedCategoryFilter = "Application";
        viewModel.SelectedLevelFilter = viewModel.LevelFilterOptions[3];
        viewModel.ApplySearchText("needle");
        IReadOnlyList<string> categories = viewModel.CategoryFilterOptions;
        IReadOnlyList<string> levels = viewModel.LevelFilterOptions;

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Same(categories, viewModel.CategoryFilterOptions);
        Assert.Same(levels, viewModel.LevelFilterOptions);
        Assert.Equal(("Application", "Error", "needle"), store.LastQuery);
        int visibleCategoryIndex = viewModel.SelectedCategoryFilterIndex;
        List<NotifyCollectionChangedAction> categoryChanges = [];
        ((INotifyCollectionChanged)categories).CollectionChanged += (_, args) => categoryChanges.Add(args.Action);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.CategoryFilterOptions))
            {
                visibleCategoryIndex = -1;
            }
            else if (args.PropertyName == nameof(viewModel.SelectedCategoryFilterIndex))
            {
                visibleCategoryIndex = viewModel.SelectedCategoryFilterIndex;
            }
        };
        store.Sources = ["Startup", "Application"];

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(2, visibleCategoryIndex);
        Assert.Same(categories, viewModel.CategoryFilterOptions);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, categoryChanges);
        Assert.Contains(NotifyCollectionChangedAction.Add, categoryChanges);
        Assert.Equal("Application", viewModel.CategoryFilterOptions[visibleCategoryIndex]);
        Assert.Equal(3, viewModel.SelectedLevelFilterIndex);
        Assert.Equal(("Application", "Error", "needle"), store.LastQuery);
    }

    /// <summary>Verifies page-owned runtime streaming publishes a bounded display row and honors cancellation.</summary>
    [Fact]
    public async Task WatchRuntimeLogsAsync_PublishesRuntimeLogUntilCancelled()
    {
        TaskCompletionSource<bool> streamAdvanced = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        LogsViewModel viewModel = new(
            GetString,
            new FakeLogManagementStore(),
            new TestApplicationErrorSink(),
            cancellationToken => StreamRuntimeLogsAsync(streamAdvanced, cancellationToken));
        using CancellationTokenSource cancellation = new();

        Task watchTask = viewModel.WatchRuntimeLogsAsync(cancellation.Token);
        await streamAdvanced.Task.WaitAsync(TimeSpan.FromSeconds(5));

        LogRecordDisplay row = Assert.Single(viewModel.RecentLogs);
        Assert.Equal("Warning", row.Record.Level);
        Assert.Equal("Core", row.Record.Source);
        Assert.Equal("runtime message", row.Message);
        Assert.Contains("Logs.Source.Core", viewModel.CategoryFilterOptions, StringComparer.Ordinal);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);
    }

    [Fact]
    public async Task WatchRuntimeLogsAsync_DefensivelyBoundsRuntimeMessage()
    {
        TaskCompletionSource<bool> streamAdvanced = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string oversized = new('x',
            MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters + 1024);
        LogsViewModel viewModel = new(
            GetString,
            new FakeLogManagementStore(),
            new TestApplicationErrorSink(),
            cancellationToken => StreamOneRuntimeLogAsync(
                oversized,
                streamAdvanced,
                cancellationToken));
        using CancellationTokenSource cancellation = new();

        Task watchTask = viewModel.WatchRuntimeLogsAsync(cancellation.Token);
        await streamAdvanced.Task.WaitAsync(TimeSpan.FromSeconds(5));

        LogRecordDisplay row = Assert.Single(viewModel.RecentLogs);
        Assert.Equal(MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters, row.Message.Length);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);
    }

    [Fact]
    public async Task WatchRuntimeLogsAsync_PublishesAndDeduplicatesServiceHostSnapshot()
    {
        int reads = 0;
        LogsViewModel viewModel = new(
            GetString,
            new FakeLogManagementStore(),
            new TestApplicationErrorSink(),
            streamRuntimeLogs: null,
            _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<IReadOnlyList<string>>(["service host entry"]);
            });
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));

        Task watchTask = viewModel.WatchRuntimeLogsAsync(cancellation.Token);
        while (Volatile.Read(ref reads) < 2)
        {
            await Task.Delay(25, cancellation.Token);
        }

        LogRecordDisplay row = Assert.Single(viewModel.RecentLogs);
        Assert.Equal("Service", row.Record.Source);
        Assert.Equal("service host entry", row.Message);
        Assert.Contains("Logs.Source.Service", viewModel.CategoryFilterOptions, StringComparer.Ordinal);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);
    }

    /// <summary>Verifies cleanup and its post-mutation snapshot never block the calling thread.</summary>
    [Fact]
    public async Task ApplyCleanupModeAsync_DoesNotBlockCaller_AndAppliesPostCleanupSnapshot()
    {
        using ManualResetEventSlim cleanupStarted = new();
        using ManualResetEventSlim releaseCleanup = new();
        int cleanupThreadId = 0;
        int summaryThreadId = 0;
        FakeLogManagementStore store = new()
        {
            ClearAllHandler = () =>
            {
                cleanupThreadId = Environment.CurrentManagedThreadId;
                cleanupStarted.Set();
                Assert.True(releaseCleanup.Wait(TimeSpan.FromSeconds(5)));
            },
            GetStorageSummaryHandler = () =>
            {
                summaryThreadId = Environment.CurrentManagedThreadId;
                return new LogStorageSnapshot(2048, 7, 1);
            },
            Logs =
            [
                new LogRecord(
                    DateTimeOffset.UtcNow,
                    "Info",
                    "Test",
                    "Visible after cleanup",
                    string.Empty),
            ],
        };
        TestApplicationErrorSink errorSink = new();
        LogsViewModel viewModel = CreateViewModel(store, errorSink);
        int callingThreadId = Environment.CurrentManagedThreadId;

        Task cleanupTask = viewModel.ApplyCleanupModeAsync(
            3,
            0,
            null,
            null,
            CancellationToken.None);

        try
        {
            Assert.True(cleanupStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(cleanupTask.IsCompleted);
            Assert.NotEqual(callingThreadId, cleanupThreadId);
        }
        finally
        {
            releaseCleanup.Set();
        }

        await cleanupTask;

        Assert.Equal(cleanupThreadId, summaryThreadId);
        Assert.Contains("7", viewModel.StorageUsageText, StringComparison.Ordinal);
        Assert.Equal("Visible after cleanup", Assert.Single(viewModel.RecentLogs).Message);
        Assert.Empty(errorSink.Errors);
    }

    /// <summary>Verifies storage failures are observed without replacing the last stable page state.</summary>
    [Fact]
    public async Task ApplyCleanupModeAsync_WhenStorageFails_ReportsErrorAndKeepsStableState()
    {
        FakeLogManagementStore store = new()
        {
            ClearAllHandler = static () => throw new InvalidOperationException("sqlite failure"),
        };
        TestApplicationErrorSink errorSink = new();
        LogsViewModel viewModel = CreateViewModel(store, errorSink);

        await viewModel.ApplyCleanupModeAsync(
            3,
            0,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(string.Empty, viewModel.StorageUsageText);
        Assert.Empty(viewModel.RecentLogs);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("logs-cleanup", error.OperationName);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Theory]
    [InlineData(0, "12 entries will be deleted.")]
    [InlineData(2, "34 entries will be deleted.")]
    [InlineData(3, "56 entries will be deleted.")]
    public async Task GetCleanupPreviewTextAsync_UsesTheSelectedCleanupScope(int mode, string expected)
    {
        FakeLogManagementStore store = new()
        {
            PreviewBeforeHandler = static _ => 12,
            PreviewCountHandler = static _ => 34,
            PreviewAllHandler = static () => 56,
        };
        LogsViewModel viewModel = CreateViewModel(store, new TestApplicationErrorSink());

        string? preview = await viewModel.GetCleanupPreviewTextAsync(
            mode,
            30,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(expected, preview);
        Assert.Equal(0, store.StorageSummaryReadCount);
        Assert.Equal(0, store.PreviewReadCount);
    }

    [Theory]
    [InlineData(double.NaN, 30)]
    [InlineData(-1, 30)]
    [InlineData(0, 30)]
    [InlineData(1, 1)]
    [InlineData(1.8, 2)]
    [InlineData(double.PositiveInfinity, 3650)]
    [InlineData(double.MaxValue, 3650)]
    public async Task DateCleanup_PreviewAndMutationUseTheSameBoundedDays(double input, int days)
    {
        DateTimeOffset previewCutoff = default;
        FakeLogManagementStore store = new()
        {
            PreviewBeforeHandler = cutoff => { previewCutoff = cutoff; return 5; },
        };
        TestApplicationErrorSink errorSink = new();
        LogsViewModel viewModel = CreateViewModel(store, errorSink);
        DateTimeOffset earliest = DateTimeOffset.UtcNow.AddDays(-days);

        await viewModel.GetCleanupPreviewTextAsync(0, input, null, null, CancellationToken.None);
        await viewModel.ApplyCleanupModeAsync(0, input, null, null, CancellationToken.None);

        DateTimeOffset latest = DateTimeOffset.UtcNow.AddDays(-days);
        Assert.InRange(previewCutoff, earliest, latest);
        Assert.InRange(store.LastCleanupCutoff, earliest, latest);
        Assert.Empty(errorSink.Errors);
    }

    [Theory]
    [InlineData(double.NaN, 1000)]
    [InlineData(-1, 1000)]
    [InlineData(0, 1000)]
    [InlineData(1, 1)]
    [InlineData(1.8, 2)]
    [InlineData(double.PositiveInfinity, 10000000)]
    [InlineData(double.MaxValue, 10000000)]
    public async Task CountCleanup_PreviewAndMutationUseTheSameBoundedCount(double input, int count)
    {
        int? previewCount = null;
        FakeLogManagementStore store = new()
        {
            PreviewCountHandler = value => { previewCount = value; return 5; },
        };
        TestApplicationErrorSink errorSink = new();
        LogsViewModel viewModel = CreateViewModel(store, errorSink);

        await viewModel.GetCleanupPreviewTextAsync(2, input, null, null, CancellationToken.None);
        await viewModel.ApplyCleanupModeAsync(2, input, null, null, CancellationToken.None);

        Assert.Equal(count, previewCount);
        Assert.Equal(count, store.LastCleanupCount);
        Assert.Empty(errorSink.Errors);
    }

    [Theory]
    [InlineData(1, "Over target:")]
    [InlineData(2, "Within target:")]
    [InlineData(3, "Within target:")]
    public async Task SizeCleanup_PreviewsActualFootprintWithoutInventingAnEntryCount(int targetMb, string prefix)
    {
        FakeLogManagementStore store = new()
        {
            GetStorageSummaryHandler = static () => new LogStorageSnapshot(2 * 1024 * 1024, 20, 0),
        };
        LogsViewModel viewModel = CreateViewModel(store, new TestApplicationErrorSink());

        string? preview = await viewModel.GetCleanupPreviewTextAsync(1, targetMb, null, null, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.StartsWith(prefix, preview, StringComparison.Ordinal);
        Assert.Contains($"{2:N2} MB", preview, StringComparison.Ordinal);
        Assert.Contains($"{targetMb:N2} MB", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("entries", preview, StringComparison.Ordinal);
        Assert.Equal(1, store.StorageSummaryReadCount);
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(0, 10)]
    [InlineData(double.PositiveInfinity, 102400)]
    [InlineData(double.MaxValue, 102400)]
    public async Task SizeCleanup_BoundsInvalidTargetsBeforeConvertingToBytes(double input, int targetMb)
    {
        FakeLogManagementStore store = new();
        TestApplicationErrorSink errorSink = new();
        LogsViewModel viewModel = CreateViewModel(store, errorSink);

        await viewModel.ApplyCleanupModeAsync(1, input, null, null, CancellationToken.None);

        Assert.Equal(targetMb * 1024L * 1024L, store.LastCleanupSize);
        Assert.Empty(errorSink.Errors);
    }

    /// <summary>Verifies filtered cleanup preview reads run away from the calling thread.</summary>
    [Fact]
    public async Task GetCleanupPreviewTextAsync_ReadsStorageOffCallingThread()
    {
        using ManualResetEventSlim previewStarted = new();
        using ManualResetEventSlim releasePreview = new();
        int previewThreadId = 0;
        FakeLogManagementStore store = new()
        {
            PreviewHandler = (_, _) =>
            {
                previewThreadId = Environment.CurrentManagedThreadId;
                previewStarted.Set();
                Assert.True(releasePreview.Wait(TimeSpan.FromSeconds(5)));
                return new LogCleanupEstimate(7, 2048);
            },
        };
        TestApplicationErrorSink errorSink = new();
        LogsViewModel viewModel = CreateViewModel(store, errorSink);
        int callingThreadId = Environment.CurrentManagedThreadId;

        Task<string?> previewTask = viewModel.GetCleanupPreviewTextAsync(
            4,
            0,
            null,
            null,
            CancellationToken.None);

        try
        {
            Assert.True(previewStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(previewTask.IsCompleted);
            Assert.NotEqual(callingThreadId, previewThreadId);
        }
        finally
        {
            releasePreview.Set();
        }

        string? preview = await previewTask;

        Assert.NotNull(preview);
        Assert.Contains("7", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("KB", preview, StringComparison.Ordinal);
        Assert.Empty(errorSink.Errors);
    }

    /// <summary>Verifies a replaced slow preview cannot overwrite the newer preview text.</summary>
    [Fact]
    public async Task CleanupPreviewSession_WhenOlderReadFinishesLast_DoesNotCommitOlderText()
    {
        using ManualResetEventSlim firstPreviewStarted = new();
        using ManualResetEventSlim releaseFirstPreview = new();
        int previewCallCount = 0;
        FakeLogManagementStore store = new()
        {
            PreviewHandler = (_, _) =>
            {
                int call = Interlocked.Increment(ref previewCallCount);
                if (call == 1)
                {
                    firstPreviewStarted.Set();
                    Assert.True(releaseFirstPreview.Wait(TimeSpan.FromSeconds(5)));
                    return new LogCleanupEstimate(1, 1024);
                }

                return new LogCleanupEstimate(2, 2048);
            },
        };
        LogsViewModel viewModel = CreateViewModel(store, new TestApplicationErrorSink());
        PageLoadSession session = new();
        string? committedPreview = null;

        Task first = session.RunAsync(async cancellationToken =>
        {
            string? text = await viewModel.GetCleanupPreviewTextAsync(
                4,
                0,
                null,
                null,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            committedPreview = text;
        });

        try
        {
            Assert.True(firstPreviewStarted.Wait(TimeSpan.FromSeconds(5)));
            await session.RunAsync(async cancellationToken =>
            {
                string? text = await viewModel.GetCleanupPreviewTextAsync(
                    4,
                    0,
                    null,
                    null,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                committedPreview = text;
            });

            Assert.NotNull(committedPreview);
            Assert.Contains("2", committedPreview, StringComparison.Ordinal);
        }
        finally
        {
            releaseFirstPreview.Set();
        }

        await first;

        Assert.NotNull(committedPreview);
        Assert.Contains("2", committedPreview, StringComparison.Ordinal);
        Assert.Equal(2, previewCallCount);
    }

    /// <summary>Verifies caller cancellation is quiet and is not reported as an application error.</summary>
    [Fact]
    public async Task GetCleanupPreviewTextAsync_WhenCallerCancels_DoesNotReportError()
    {
        using ManualResetEventSlim previewStarted = new();
        using ManualResetEventSlim releasePreview = new();
        FakeLogManagementStore store = new()
        {
            PreviewHandler = (_, _) =>
            {
                previewStarted.Set();
                Assert.True(releasePreview.Wait(TimeSpan.FromSeconds(5)));
                return new LogCleanupEstimate(1, 1024);
            },
        };
        TestApplicationErrorSink errorSink = new();
        LogsViewModel viewModel = CreateViewModel(store, errorSink);
        using CancellationTokenSource cancellation = new();
        Task<string?> previewTask = viewModel.GetCleanupPreviewTextAsync(
            4,
            0,
            null,
            null,
            cancellation.Token);

        try
        {
            Assert.True(previewStarted.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
        }
        finally
        {
            releasePreview.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previewTask);
        Assert.Empty(errorSink.Errors);
    }

    /// <summary>Verifies preview failures are reported while callers retain existing stable text.</summary>
    [Fact]
    public async Task GetCleanupPreviewTextAsync_WhenStorageFails_ReturnsNullAndReportsError()
    {
        FakeLogManagementStore store = new()
        {
            PreviewHandler = static (_, _) => throw new InvalidOperationException("sqlite failure"),
        };
        TestApplicationErrorSink errorSink = new();
        LogsViewModel viewModel = CreateViewModel(store, errorSink);

        string? preview = await viewModel.GetCleanupPreviewTextAsync(
            4,
            0,
            null,
            null,
            CancellationToken.None);

        Assert.Null(preview);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("logs-cleanup-preview", error.OperationName);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    private static LogsViewModel CreateViewModel(
        ILogManagementStore store,
        TestApplicationErrorSink errorSink)
    {
        return new LogsViewModel(GetString, store, errorSink);
    }

    private static async IAsyncEnumerable<(string Level, string Message)> StreamRuntimeLogsAsync(
        TaskCompletionSource<bool> streamAdvanced,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return ("Warning", "runtime message");
        streamAdvanced.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<(string Level, string Message)> StreamOneRuntimeLogAsync(
        string message,
        TaskCompletionSource<bool> streamAdvanced,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return ("Info", message);
        streamAdvanced.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static string GetString(string key)
    {
        return key switch
        {
            "Logs.StorageUsage.Format" => "{0} | {1} | {2}",
            "Logs.Filter.AllLevels" => "All levels",
            "Logs.Filter.AllCategories" => "All categories",
            "Logs.Cleanup.Preview.Loading" => "Calculating cleanup…",
            "Logs.Cleanup.Preview.Failed" => "Couldn't calculate cleanup.",
            "Logs.Cleanup.Preview.Count" => "{0:N0} entries will be deleted.",
            "Logs.Cleanup.Preview.SizeTarget" => "Over target: {0}, {1}",
            "Logs.Cleanup.Preview.SizeSatisfied" => "Within target: {0}, {1}",
            _ => key,
        };
    }

    private sealed class FakeLogManagementStore : ILogManagementStore
    {
        private int _previewReadCount;
        private int _storageSummaryReadCount;

        public Action ClearAllHandler { get; init; } = static () => { };

        public Func<LogStorageSnapshot> GetStorageSummaryHandler { get; init; } =
            static () => default;

        public Func<string?, string?, LogCleanupEstimate> PreviewHandler { get; init; } =
            static (_, _) => default;

        public Func<DateTimeOffset, long> PreviewBeforeHandler { get; init; } = static _ => 0;

        public Func<int, long> PreviewCountHandler { get; init; } = static _ => 0;

        public Func<long> PreviewAllHandler { get; init; } = static () => 0;

        public DateTimeOffset LastCleanupCutoff { get; private set; }

        public int? LastCleanupCount { get; private set; }

        public long? LastCleanupSize { get; private set; }

        public IReadOnlyList<LogRecord> Logs { get; init; } = [];

        public IReadOnlyList<string> Sources { get; set; } = [];

        public (string? Source, string? Level, string? SearchText) LastQuery { get; private set; }

        public int PreviewReadCount => Volatile.Read(ref _previewReadCount);

        public int StorageSummaryReadCount => Volatile.Read(ref _storageSummaryReadCount);

        public LogStorageSnapshot GetStorageSummary()
        {
            Interlocked.Increment(ref _storageSummaryReadCount);
            return GetStorageSummaryHandler();
        }

        public IReadOnlyList<string> GetLogSources()
        {
            return Sources;
        }

        public IReadOnlyList<LogRecord> GetLogs(
            int limit,
            string? source,
            string? level,
            string? searchText)
        {
            LastQuery = (source, level, searchText);
            return Logs;
        }

        public void CleanupBefore(DateTimeOffset cutoff)
        {
            LastCleanupCutoff = cutoff;
        }

        public void CleanupToSize(long targetSizeBytes)
        {
            LastCleanupSize = targetSizeBytes;
        }

        public void CleanupToLogCount(int maxLogCount)
        {
            LastCleanupCount = maxLogCount;
        }

        public void ClearAll()
        {
            ClearAllHandler();
        }

        public long CleanupLogs(string? level, string? source)
        {
            return 0;
        }

        public LogCleanupEstimate PreviewLogCleanup(string? level, string? source)
        {
            Interlocked.Increment(ref _previewReadCount);
            return PreviewHandler(level, source);
        }

        public long PreviewCleanupBefore(DateTimeOffset cutoff) => PreviewBeforeHandler(cutoff);

        public long PreviewCleanupToLogCount(int maxLogCount) => PreviewCountHandler(maxLogCount);

        public long PreviewClearAll() => PreviewAllHandler();
    }
}
