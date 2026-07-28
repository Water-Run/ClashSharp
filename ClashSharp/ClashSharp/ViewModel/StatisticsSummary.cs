namespace ClashSharp.ViewModel;

/// <summary>Statistics summary used by <see cref="StatisticsViewModel"/>.</summary>
/// <param name="TotalUploadBytes">Total uploaded byte count.</param>
/// <param name="TotalDownloadBytes">Total downloaded byte count.</param>
/// <param name="ConnectionCount">Total connection count.</param>
/// <param name="SnapshotCount">Total snapshot count.</param>
/// <param name="ProfileCount">Profile aggregation row count.</param>
/// <param name="NodeCount">Node aggregation row count.</param>
/// <param name="NodeHealthCount">Node health row count.</param>
/// <param name="RuleCount">Rule row count.</param>
/// <remarks>
/// Invariants: Count and byte values are non-negative snapshots.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
internal readonly record struct StatisticsSummary(
    long TotalUploadBytes,
    long TotalDownloadBytes,
    long ConnectionCount,
    long SnapshotCount,
    long ProfileCount,
    long NodeCount,
    long NodeHealthCount,
    long RuleCount);
