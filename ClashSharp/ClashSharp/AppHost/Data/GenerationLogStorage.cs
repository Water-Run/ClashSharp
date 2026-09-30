using System;
using System.Collections.Generic;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Resolves the current repository for every call and retains its generation until that operation finishes.</summary>
internal sealed class GenerationLogStorage(DataGenerationManager generations) : ILogStorage
{
    private readonly DataGenerationManager _generations = generations ?? throw new ArgumentNullException(nameof(generations));

    public LogStorageSummary GetStorageSummary()
    {
        return _generations.Execute<ILogStorage, LogStorageSummary>((storage, _) => storage.GetStorageSummary());
    }

    public TrafficStatisticsSummary GetTrafficStatisticsSummary()
    {
        return _generations.Execute<ILogStorage, TrafficStatisticsSummary>((storage, _) => storage.GetTrafficStatisticsSummary());
    }

    public long GetTrafficBytesSince(DateTimeOffset cutoff)
    {
        return _generations.Execute<ILogStorage, long>((storage, _) => storage.GetTrafficBytesSince(cutoff));
    }

    public IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit)
    {
        return _generations.Execute<ILogStorage, IReadOnlyList<TrafficStatisticRow>>((storage, _) => storage.GetProfileTrafficRows(limit));
    }

    public IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit)
    {
        return _generations.Execute<ILogStorage, IReadOnlyList<TrafficStatisticRow>>((storage, _) => storage.GetDailyTrafficRows(limit));
    }

    public IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit)
    {
        return _generations.Execute<ILogStorage, IReadOnlyList<TrafficStatisticRow>>((storage, _) => storage.GetNodeTrafficRows(limit));
    }

    public void UpsertNodeHealth(string nodeName, string regionCode, int? latencyMilliseconds)
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.UpsertNodeHealth(nodeName, regionCode, latencyMilliseconds));
    }

    public int? GetNodeLatencyMilliseconds(string nodeName)
    {
        return _generations.Execute<ILogStorage, int?>((storage, _) => storage.GetNodeLatencyMilliseconds(nodeName));
    }

    public void EnsureRuleHitRows(IEnumerable<RulePreview> rules)
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.EnsureRuleHitRows(rules));
    }

    public void IncrementRuleHit(string ruleName, long increment = 1)
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.IncrementRuleHit(ruleName, increment));
    }

    public int AppendConnectionSnapshot(IEnumerable<ActiveConnection> connections)
    {
        return _generations.Execute<ILogStorage, int>((storage, _) => storage.AppendConnectionSnapshot(connections));
    }

    public int AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot)
    {
        return _generations.Execute<ILogStorage, int>((storage, _) => storage.AppendTrafficSnapshot(snapshot));
    }

    public IReadOnlyDictionary<string, long> GetRuleHitCounts()
    {
        return _generations.Execute<ILogStorage, IReadOnlyDictionary<string, long>>((storage, _) => storage.GetRuleHitCounts());
    }

    public void AppendLog(string level, string source, string message, string? detail)
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.AppendLog(level, source, message, detail));
    }

    public IReadOnlyList<LogRecord> GetRecentLogs(int limit)
    {
        return _generations.Execute<ILogStorage, IReadOnlyList<LogRecord>>((storage, _) => storage.GetRecentLogs(limit));
    }

    public IReadOnlyList<LogRecord> GetRecentLogs(string source, int limit)
    {
        return _generations.Execute<ILogStorage, IReadOnlyList<LogRecord>>((storage, _) => storage.GetRecentLogs(source, limit));
    }

    public IReadOnlyList<LogRecord> GetLogs(int limit, string? source = null, string? level = null, string? searchText = null)
    {
        return _generations.Execute<ILogStorage, IReadOnlyList<LogRecord>>((storage, _) => storage.GetLogs(limit, source, level, searchText));
    }

    public LogCleanupPreview PreviewLogCleanup(string? level = null, string? source = null)
    {
        return _generations.Execute<ILogStorage, LogCleanupPreview>((storage, _) => storage.PreviewLogCleanup(level, source));
    }

    public long CleanupLogs(string? level = null, string? source = null)
    {
        return _generations.Execute<ILogStorage, long>((storage, _) => storage.CleanupLogs(level, source));
    }

    public IReadOnlyList<string> GetLogSources()
    {
        return _generations.Execute<ILogStorage, IReadOnlyList<string>>((storage, _) => storage.GetLogSources());
    }

    public void ExportDatabase(string destinationPath)
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.ExportDatabase(destinationPath));
    }

    public void CleanupBefore(DateTimeOffset cutoff)
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.CleanupBefore(cutoff));
    }

    public void CleanupToSize(long targetSizeBytes)
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.CleanupToSize(targetSizeBytes));
    }

    public void CleanupToLogCount(long maxLogCount)
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.CleanupToLogCount(maxLogCount));
    }

    public void ClearAll()
    {
        _generations.Execute<ILogStorage>((storage, _) => storage.ClearAll());
    }

    public long PreviewCleanupBefore(DateTimeOffset cutoff)
    {
        return _generations.Execute<ILogStorage, long>((storage, _) => storage.PreviewCleanupBefore(cutoff));
    }

    public long PreviewCleanupToLogCount(long maxLogCount)
    {
        return _generations.Execute<ILogStorage, long>((storage, _) => storage.PreviewCleanupToLogCount(maxLogCount));
    }

    public long PreviewClearAll()
    {
        return _generations.Execute<ILogStorage, long>((storage, _) => storage.PreviewClearAll());
    }
}
