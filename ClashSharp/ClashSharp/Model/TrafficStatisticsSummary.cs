namespace ClashSharp.Model;

/// <summary>Summarizes long-term traffic and aggregation records stored in SQLite.</summary>
/// <param name="TotalUploadBytes">Total uploaded bytes estimated from connection records.</param>
/// <param name="TotalDownloadBytes">Total downloaded bytes estimated from connection records.</param>
/// <param name="ConnectionCount">Total connection record count.</param>
/// <param name="SnapshotCount">Total traffic snapshot count.</param>
/// <param name="ProfileCount">Number of profile traffic aggregation rows.</param>
/// <param name="NodeCount">Number of node traffic aggregation rows.</param>
/// <param name="NodeHealthCount">Number of node health rows.</param>
/// <param name="RuleCount">Number of rule hit aggregation rows.</param>
/// <remarks>
/// Invariants: Count and byte values are non-negative and reflect the database state at query time.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct TrafficStatisticsSummary(
    long TotalUploadBytes,
    long TotalDownloadBytes,
    long ConnectionCount,
    long SnapshotCount,
    long ProfileCount,
    long NodeCount,
    long NodeHealthCount,
    long RuleCount);
