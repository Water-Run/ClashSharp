namespace ClashSharp.Model;

/// <summary>Realtime traffic counters calculated from active mihomo connections.</summary>
internal readonly record struct RuntimeTrafficRateSnapshot(
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    int ActiveConnectionCount,
    long SessionUploadBytes,
    long SessionDownloadBytes);
