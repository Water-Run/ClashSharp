namespace ClashSharp.ViewModel;

/// <summary>Storage metrics required by the logs presentation model.</summary>
internal readonly record struct LogStorageSnapshot(
    long DatabaseSizeBytes,
    long LogCount,
    long ConnectionCount);
