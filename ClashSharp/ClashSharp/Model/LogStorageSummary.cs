namespace ClashSharp.Model;

/// <summary>Summarizes the current SQLite log storage footprint and record counts.</summary>
/// <param name="DatabasePath">Absolute path to the SQLite database file; never null.</param>
/// <param name="DatabaseSizeBytes">Current SQLite footprint in bytes, including WAL sidecar files when present.</param>
/// <param name="LogCount">Total count of log records currently stored.</param>
/// <param name="ConnectionCount">Total count of connection records currently stored.</param>
/// <remarks>
/// Invariants: Count values are non-negative and reflect the database state at query time.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct LogStorageSummary(
    string DatabasePath,
    long DatabaseSizeBytes,
    long LogCount,
    long ConnectionCount);
