using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Verifies asynchronous log maintenance and preview presentation boundaries.</summary>
public sealed class LogsViewModelTests
{
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

    /// <summary>Verifies modes with no implemented estimate preserve existing text without touching SQLite.</summary>
    [Fact]
    public async Task GetCleanupPreviewTextAsync_ForNonStorageModes_DoesNotReadStorage()
    {
        FakeLogManagementStore store = new();
        LogsViewModel viewModel = CreateViewModel(store, new TestApplicationErrorSink());

        string? preview = await viewModel.GetCleanupPreviewTextAsync(
            0,
            30,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(viewModel.CleanupPreviewPlaceholderText, preview);
        Assert.Equal(0, store.StorageSummaryReadCount);
        Assert.Equal(0, store.PreviewReadCount);
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
        Assert.Contains("KB", preview, StringComparison.Ordinal);
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

    private static string GetString(string key)
    {
        return key switch
        {
            "Logs.StorageUsage.Format" => "{0} | {1} | {2}",
            "Logs.Filter.AllLevels" => "All levels",
            "Logs.Filter.AllCategories" => "All categories",
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

        public IReadOnlyList<LogRecord> Logs { get; init; } = [];

        public int PreviewReadCount => Volatile.Read(ref _previewReadCount);

        public int StorageSummaryReadCount => Volatile.Read(ref _storageSummaryReadCount);

        public LogStorageSnapshot GetStorageSummary()
        {
            Interlocked.Increment(ref _storageSummaryReadCount);
            return GetStorageSummaryHandler();
        }

        public IReadOnlyList<string> GetLogSources()
        {
            return [];
        }

        public IReadOnlyList<LogRecord> GetLogs(
            int limit,
            string? source,
            string? level,
            string? searchText)
        {
            return Logs;
        }

        public void CleanupBefore(DateTimeOffset cutoff)
        {
        }

        public void CleanupToSize(long targetSizeBytes)
        {
        }

        public void CleanupToLogCount(int maxLogCount)
        {
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
    }
}
