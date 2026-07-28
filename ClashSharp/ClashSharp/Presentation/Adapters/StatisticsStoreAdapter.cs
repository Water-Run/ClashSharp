using System;
using System.Collections.Generic;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="LogStorageService"/> to statistics reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null log storage service.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads persistent statistics storage.
/// </remarks>
internal sealed class StatisticsStoreAdapter : IStatisticsStore
{
    /// <summary>Wrapped log storage service.</summary>
    private readonly LogStorageService _logStorage;

    /// <summary>Initializes a statistics store adapter.</summary>
    /// <param name="logStorage">Log storage service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logStorage"/> is null.</exception>
    public StatisticsStoreAdapter(LogStorageService logStorage)
    {
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
    }

    /// <summary>Gets the current aggregate statistics summary.</summary>
    /// <returns>Current aggregate statistics summary.</returns>
    public StatisticsSummary GetTrafficStatisticsSummary()
    {
        TrafficStatisticsSummary summary = _logStorage.GetTrafficStatisticsSummary();
        return new StatisticsSummary(
            summary.TotalUploadBytes,
            summary.TotalDownloadBytes,
            summary.ConnectionCount,
            summary.SnapshotCount,
            summary.ProfileCount,
            summary.NodeCount,
            summary.NodeHealthCount,
            summary.RuleCount);
    }

    /// <summary>Gets profile traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Profile traffic rows.</returns>
    public IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit)
    {
        return _logStorage.GetProfileTrafficRows(limit);
    }

    /// <summary>Gets daily traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Daily traffic rows.</returns>
    public IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit)
    {
        return _logStorage.GetDailyTrafficRows(limit);
    }

    /// <summary>Gets node traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Node traffic rows.</returns>
    public IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit)
    {
        return _logStorage.GetNodeTrafficRows(limit);
    }
}
