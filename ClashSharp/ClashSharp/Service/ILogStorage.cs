using System;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides complete operations on logs, traffic, and rule statistics, without exposing repository ownership.</summary>
/// <remarks>Results are detached values. Consumers must not retain file handles or dispose the underlying generation.</remarks>
internal interface ILogStorage
{
    LogStorageSummary GetStorageSummary();

    TrafficStatisticsSummary GetTrafficStatisticsSummary();

    long GetTrafficBytesSince(DateTimeOffset cutoff);

    IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit);

    IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit);

    IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit);

    void UpsertNodeHealth(string nodeName, string regionCode, int? latencyMilliseconds);

    int? GetNodeLatencyMilliseconds(string nodeName);

    void EnsureRuleHitRows(IEnumerable<RulePreview> rules);

    void IncrementRuleHit(string ruleName, long increment = 1);

    int AppendConnectionSnapshot(IEnumerable<ActiveConnection> connections);

    int AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot);

    IReadOnlyDictionary<string, long> GetRuleHitCounts();

    void AppendLog(string level, string source, string message, string? detail);

    IReadOnlyList<LogRecord> GetRecentLogs(int limit);

    IReadOnlyList<LogRecord> GetRecentLogs(string source, int limit);

    IReadOnlyList<LogRecord> GetLogs(int limit, string? source = null, string? level = null, string? searchText = null);

    LogCleanupPreview PreviewLogCleanup(string? level = null, string? source = null);

    long CleanupLogs(string? level = null, string? source = null);

    IReadOnlyList<string> GetLogSources();

    void ExportDatabase(string destinationPath);

    void CleanupBefore(DateTimeOffset cutoff);

    void CleanupToSize(long targetSizeBytes);

    void CleanupToLogCount(long maxLogCount);

    void ClearAll();

    long PreviewCleanupBefore(DateTimeOffset cutoff);

    long PreviewCleanupToLogCount(long maxLogCount);

    long PreviewClearAll();
}
