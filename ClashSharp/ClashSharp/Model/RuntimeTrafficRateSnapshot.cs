/*
 * Runtime Traffic Rate Snapshot
 * Describes current connection count and session traffic rates
 *
 * @author: WaterRun
 * @file: Model/RuntimeTrafficRateSnapshot.cs
 * @date: 2026-06-29
 */

namespace ClashSharp.Model;

/// <summary>Realtime traffic counters calculated from active mihomo connections.</summary>
internal readonly record struct RuntimeTrafficRateSnapshot(
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    int ActiveConnectionCount,
    long SessionUploadBytes,
    long SessionDownloadBytes);
