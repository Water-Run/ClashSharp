/*
 * Traffic Statistic Row Model
 * Represents one traffic statistics row for profile, time, or node breakdown views
 *
 * @author: WaterRun
 * @file: Model/TrafficStatisticRow.cs
 * @date: 2026-06-15
 */

using System;

namespace ClashSharp.Model;

/// <summary>Represents one traffic statistics row for profile, time, or node breakdown views.</summary>
/// <param name="Label">Row label such as profile name, date, or node name; never null.</param>
/// <param name="UploadBytes">Uploaded byte count.</param>
/// <param name="DownloadBytes">Downloaded byte count.</param>
/// <param name="SampleCount">Number of source records represented by the row.</param>
/// <param name="UpdatedAt">Last update time for the row.</param>
/// <remarks>
/// Invariants: String values are never null; byte counts and sample counts are non-negative.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct TrafficStatisticRow(
    string Label,
    long UploadBytes,
    long DownloadBytes,
    long SampleCount,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Gets a compact uploaded byte display.</summary>
    /// <value>Formatted uploaded byte count; never null.</value>
    public string UploadDisplay => FormatByteCount(UploadBytes);

    /// <summary>Gets a compact downloaded byte display.</summary>
    /// <value>Formatted downloaded byte count; never null.</value>
    public string DownloadDisplay => FormatByteCount(DownloadBytes);

    /// <summary>Gets a compact total traffic display.</summary>
    /// <value>Formatted total byte count; never null.</value>
    public string TotalDisplay => FormatByteCount(UploadBytes + DownloadBytes);

    /// <summary>Gets a compact sample count display.</summary>
    /// <value>Formatted sample count; never null.</value>
    public string SampleCountDisplay => $"{SampleCount:N0}";

    /// <summary>Gets a local-time update display.</summary>
    /// <value>Formatted local update time; never null.</value>
    public string UpdatedAtDisplay => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Formats a byte count for compact UI display.</summary>
    /// <param name="bytes">Byte count. Must be non-negative.</param>
    /// <returns>Formatted byte count.</returns>
    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N1} {units[unitIndex]}";
    }
}
