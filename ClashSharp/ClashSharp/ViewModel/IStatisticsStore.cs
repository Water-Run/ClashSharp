using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Statistics data contract used by <see cref="StatisticsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Returned row lists are safe to bind directly.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May read persistent statistics storage.
/// </remarks>
internal interface IStatisticsStore
{
    /// <summary>Gets the current aggregate statistics summary.</summary>
    /// <returns>Current aggregate statistics summary.</returns>
    StatisticsSummary GetTrafficStatisticsSummary();

    /// <summary>Gets profile traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Profile traffic rows.</returns>
    IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit);

    /// <summary>Gets daily traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Daily traffic rows.</returns>
    IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit);

    /// <summary>Gets node traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Node traffic rows.</returns>
    IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit);
}
