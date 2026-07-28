using System;
using System.Collections.Generic;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts SQLite storage to presentation-owned log contracts.</summary>
internal sealed class LogManagementStoreAdapter : ILogManagementStore
{
    private readonly LogStorageService _logStorage;

    public LogManagementStoreAdapter(LogStorageService logStorage)
    {
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
    }

    public LogStorageSnapshot GetStorageSummary()
    {
        LogStorageSummary summary = _logStorage.GetStorageSummary();
        return new LogStorageSnapshot(
            summary.DatabaseSizeBytes,
            summary.LogCount,
            summary.ConnectionCount);
    }

    public IReadOnlyList<string> GetLogSources()
    {
        return _logStorage.GetLogSources();
    }

    public IReadOnlyList<LogRecord> GetLogs(
        int limit,
        string? source,
        string? level,
        string? searchText)
    {
        return _logStorage.GetLogs(limit, source, level, searchText);
    }

    public void CleanupBefore(DateTimeOffset cutoff)
    {
        _logStorage.CleanupBefore(cutoff);
    }

    public void CleanupToSize(long targetSizeBytes)
    {
        _logStorage.CleanupToSize(targetSizeBytes);
    }

    public void CleanupToLogCount(int maxLogCount)
    {
        _logStorage.CleanupToLogCount(maxLogCount);
    }

    public void ClearAll()
    {
        _logStorage.ClearAll();
    }

    public long CleanupLogs(string? level, string? source)
    {
        return _logStorage.CleanupLogs(level, source);
    }

    public LogCleanupEstimate PreviewLogCleanup(string? level, string? source)
    {
        LogCleanupPreview preview = _logStorage.PreviewLogCleanup(level, source);
        return new LogCleanupEstimate(preview.EntryCount, preview.EstimatedSizeBytes);
    }
}
