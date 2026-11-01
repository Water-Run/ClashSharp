using System;
using System.Collections.Generic;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts SQLite storage to presentation-owned log contracts.</summary>
internal sealed class LogManagementStoreAdapter : ILogManagementStore
{
    private readonly OperationNotificationRunner _notifications;

    private readonly ILogStorage _logStorage;

    public LogManagementStoreAdapter(ILogStorage logStorage, IUserOperationNotifications? notifications = null)
    {
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
        _notifications = new(notifications);
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
        _notifications.Execute(() => _logStorage.CleanupBefore(cutoff),
            "Nav.Logs", "Notification.Logs.Cleaned", "Application.UnexpectedError");
    }

    public void CleanupToSize(long targetSizeBytes)
    {
        _notifications.Execute(() => _logStorage.CleanupToSize(targetSizeBytes),
            "Nav.Logs", "Notification.Logs.Cleaned", "Application.UnexpectedError");
    }

    public void CleanupToLogCount(int maxLogCount)
    {
        _notifications.Execute(() => _logStorage.CleanupToLogCount(maxLogCount),
            "Nav.Logs", "Notification.Logs.Cleaned", "Application.UnexpectedError");
    }

    public void ClearAll()
    {
        _notifications.Execute(() => _logStorage.ClearAll(),
            "Nav.Logs", "Notification.Logs.Cleaned", "Application.UnexpectedError");
    }

    public long CleanupLogs(string? level, string? source)
    {
        return _notifications.Execute(() => _logStorage.CleanupLogs(level, source),
            "Nav.Logs", "Notification.Logs.Cleaned", "Application.UnexpectedError");
    }

    public LogCleanupEstimate PreviewLogCleanup(string? level, string? source)
    {
        LogCleanupPreview preview = _logStorage.PreviewLogCleanup(level, source);
        return new LogCleanupEstimate(preview.EntryCount, preview.EstimatedSizeBytes);
    }

    public long PreviewCleanupBefore(DateTimeOffset cutoff) => _logStorage.PreviewCleanupBefore(cutoff);

    public long PreviewCleanupToLogCount(int maxLogCount) => _logStorage.PreviewCleanupToLogCount(maxLogCount);

    public long PreviewClearAll() => _logStorage.PreviewClearAll();
}
