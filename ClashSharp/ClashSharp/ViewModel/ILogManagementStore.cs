using System;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Log query and maintenance boundary required by the logs presentation model.</summary>
internal interface ILogManagementStore
{
    LogStorageSnapshot GetStorageSummary();

    IReadOnlyList<string> GetLogSources();

    IReadOnlyList<LogRecord> GetLogs(
        int limit,
        string? source,
        string? level,
        string? searchText);

    void CleanupBefore(DateTimeOffset cutoff);

    void CleanupToSize(long targetSizeBytes);

    void CleanupToLogCount(int maxLogCount);

    void ClearAll();

    long CleanupLogs(string? level, string? source);

    LogCleanupEstimate PreviewLogCleanup(string? level, string? source);
}
