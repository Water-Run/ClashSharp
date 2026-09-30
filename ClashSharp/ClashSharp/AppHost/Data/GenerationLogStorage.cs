using System;
using System.Collections.Generic;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Resolves the current repository for every call and retains its generation until that operation finishes.</summary>
internal sealed class GenerationLogStorage : ILogStorage
{
    private readonly Func<DataGenerationManager> _getGenerations;
    private readonly Func<ILogStorage>? _startupRecovery;

    public GenerationLogStorage(DataGenerationManager generations) : this(() => generations) { }
    internal GenerationLogStorage(Func<DataGenerationManager> getGenerations, Func<ILogStorage>? startupRecovery = null)
    {
        _getGenerations = getGenerations ?? throw new ArgumentNullException(nameof(getGenerations));
        _startupRecovery = startupRecovery;
    }

    private TResult Execute<TResult>(Func<ILogStorage, TResult> operation)
    {
        DataGenerationManager generations = _getGenerations();
        if (generations.IsAwaitingInitialization && _startupRecovery is not null) { return operation(_startupRecovery()); }
        return generations.Execute<ILogStorage, TResult>((storage, _) => operation(storage));
    }

    private void Execute(Action<ILogStorage> operation)
    {
        DataGenerationManager generations = _getGenerations();
        if (generations.IsAwaitingInitialization && _startupRecovery is not null) { operation(_startupRecovery()); return; }
        generations.Execute<ILogStorage>((storage, _) => operation(storage));
    }

    public LogStorageSummary GetStorageSummary()
    {
        return Execute<LogStorageSummary>(storage => storage.GetStorageSummary());
    }

    public TrafficStatisticsSummary GetTrafficStatisticsSummary()
    {
        return Execute<TrafficStatisticsSummary>(storage => storage.GetTrafficStatisticsSummary());
    }

    public long GetTrafficBytesSince(DateTimeOffset cutoff)
    {
        return Execute<long>(storage => storage.GetTrafficBytesSince(cutoff));
    }

    public IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit)
    {
        return Execute<IReadOnlyList<TrafficStatisticRow>>(storage => storage.GetProfileTrafficRows(limit));
    }

    public IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit)
    {
        return Execute<IReadOnlyList<TrafficStatisticRow>>(storage => storage.GetDailyTrafficRows(limit));
    }

    public IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit)
    {
        return Execute<IReadOnlyList<TrafficStatisticRow>>(storage => storage.GetNodeTrafficRows(limit));
    }

    public void UpsertNodeHealth(string nodeName, string regionCode, int? latencyMilliseconds)
    {
        Execute(storage => storage.UpsertNodeHealth(nodeName, regionCode, latencyMilliseconds));
    }

    public int? GetNodeLatencyMilliseconds(string nodeName)
    {
        return Execute<int?>(storage => storage.GetNodeLatencyMilliseconds(nodeName));
    }

    public void EnsureRuleHitRows(IEnumerable<RulePreview> rules)
    {
        Execute(storage => storage.EnsureRuleHitRows(rules));
    }

    public void IncrementRuleHit(string ruleName, long increment = 1)
    {
        Execute(storage => storage.IncrementRuleHit(ruleName, increment));
    }

    public int AppendConnectionSnapshot(IEnumerable<ActiveConnection> connections)
    {
        return Execute<int>(storage => storage.AppendConnectionSnapshot(connections));
    }

    public int AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot)
    {
        return Execute<int>(storage => storage.AppendTrafficSnapshot(snapshot));
    }

    public IReadOnlyDictionary<string, long> GetRuleHitCounts()
    {
        return Execute<IReadOnlyDictionary<string, long>>(storage => storage.GetRuleHitCounts());
    }

    public void AppendLog(string level, string source, string message, string? detail)
    {
        Execute(storage => storage.AppendLog(level, source, message, detail));
    }

    public IReadOnlyList<LogRecord> GetRecentLogs(int limit)
    {
        return Execute<IReadOnlyList<LogRecord>>(storage => storage.GetRecentLogs(limit));
    }

    public IReadOnlyList<LogRecord> GetRecentLogs(string source, int limit)
    {
        return Execute<IReadOnlyList<LogRecord>>(storage => storage.GetRecentLogs(source, limit));
    }

    public IReadOnlyList<LogRecord> GetLogs(int limit, string? source = null, string? level = null, string? searchText = null)
    {
        return Execute<IReadOnlyList<LogRecord>>(storage => storage.GetLogs(limit, source, level, searchText));
    }

    public LogCleanupPreview PreviewLogCleanup(string? level = null, string? source = null)
    {
        return Execute<LogCleanupPreview>(storage => storage.PreviewLogCleanup(level, source));
    }

    public long CleanupLogs(string? level = null, string? source = null)
    {
        return Execute<long>(storage => storage.CleanupLogs(level, source));
    }

    public IReadOnlyList<string> GetLogSources()
    {
        return Execute<IReadOnlyList<string>>(storage => storage.GetLogSources());
    }

    public void ExportDatabase(string destinationPath)
    {
        Execute(storage => storage.ExportDatabase(destinationPath));
    }

    public void CleanupBefore(DateTimeOffset cutoff)
    {
        Execute(storage => storage.CleanupBefore(cutoff));
    }

    public void CleanupToSize(long targetSizeBytes)
    {
        Execute(storage => storage.CleanupToSize(targetSizeBytes));
    }

    public void CleanupToLogCount(long maxLogCount)
    {
        Execute(storage => storage.CleanupToLogCount(maxLogCount));
    }

    public void ClearAll()
    {
        Execute(storage => storage.ClearAll());
    }

    public long PreviewCleanupBefore(DateTimeOffset cutoff)
    {
        return Execute<long>(storage => storage.PreviewCleanupBefore(cutoff));
    }

    public long PreviewCleanupToLogCount(long maxLogCount)
    {
        return Execute<long>(storage => storage.PreviewCleanupToLogCount(maxLogCount));
    }

    public long PreviewClearAll()
    {
        return Execute<long>(storage => storage.PreviewClearAll());
    }
}
