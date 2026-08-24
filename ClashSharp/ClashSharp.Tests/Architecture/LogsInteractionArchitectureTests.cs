namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the cancellable asynchronous boundary for SQLite-backed logs interactions.</summary>
public sealed class LogsInteractionArchitectureTests
{
    private static readonly string ApplicationRoot = Path.Combine(
        FindRepositoryRoot(),
        "ClashSharp",
        "ClashSharp");

    /// <summary>Verifies page events cannot synchronously execute SQLite preview or cleanup work.</summary>
    [Fact]
    public void LogsPage_UsesObservedCancellableAsyncInteractions()
    {
        string pageSource = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "View",
            "Logs.xaml.cs"));
        string compositionSource = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "Presentation",
            "Composition",
            "LogsPageComposition.cs"));

        Assert.Contains("CleanupPreviewDebounceDelay", pageSource, StringComparison.Ordinal);
        Assert.Contains("_cleanupPreviewSession", pageSource, StringComparison.Ordinal);
        Assert.Contains("_cleanupPreviewSession.Cancel();", pageSource, StringComparison.Ordinal);
        Assert.Contains("_runtimeLogStreamSession", pageSource, StringComparison.Ordinal);
        Assert.Contains("_viewModel.WatchRuntimeLogsAsync", pageSource, StringComparison.Ordinal);
        Assert.Contains("context.MihomoService.ReadHostLogsAsync", compositionSource, StringComparison.Ordinal);
        Assert.Contains("_runtimeLogStreamSession.Cancel();", pageSource, StringComparison.Ordinal);
        Assert.Contains("RunObservedPageEventAsync(", pageSource, StringComparison.Ordinal);
        Assert.Contains("RunLatestPageOperationAsync(", pageSource, StringComparison.Ordinal);
        Assert.Contains("dialog.ShowManagedAsync(pageToken)", pageSource, StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.GetCleanupPreviewTextAsync(",
            pageSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.ApplyCleanupModeAsync(",
            pageSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_viewModel.GetCleanupPreviewText(",
            pageSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_viewModel.ApplyCleanupMode(",
            pageSource,
            StringComparison.Ordinal);
    }

    /// <summary>Verifies cleanup refresh and preview reads stay inside the background snapshot boundary.</summary>
    [Fact]
    public void LogsViewModel_UsesBackgroundSnapshotBoundaryForSqliteWork()
    {
        string viewModelSource = File.ReadAllText(Path.Combine(
            ApplicationRoot,
            "ViewModel",
            "LogsViewModel.cs"));

        Assert.Contains(
            "Task ApplyCleanupModeAsync(",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Task<string?> GetCleanupPreviewTextAsync(",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "return ReadLoadSnapshot(loadRequest);",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"logs-cleanup-preview\"",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Task WatchRuntimeLogsAsync(",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_liveRuntimeLogs.Count > VisibleLogLimit",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("public void RefreshLogs(", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("public void ApplyCleanupMode(", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("public string GetCleanupPreviewText(", viewModelSource, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ClashSharp repository root.");
    }
}
