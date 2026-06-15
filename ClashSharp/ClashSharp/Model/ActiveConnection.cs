/*
 * Active Connection Model
 * Represents one active mihomo connection row exposed by the external controller
 *
 * @author: WaterRun
 * @file: Model/ActiveConnection.cs
 * @date: 2026-06-15
 */

using System;
using System.Globalization;
using ClashSharp.Service;

namespace ClashSharp.Model;

/// <summary>Represents one active mihomo connection row exposed by the external controller.</summary>
/// <param name="Id">Connection identifier; never null.</param>
/// <param name="ProcessName">Originating process name when reported; never null.</param>
/// <param name="Host">Destination host or address; never null.</param>
/// <param name="RuleName">Matched rule name or type; never null.</param>
/// <param name="RulePayload">Matched rule payload; never null.</param>
/// <param name="ProxyName">Selected proxy chain display text; never null.</param>
/// <param name="UploadBytes">Uploaded byte count.</param>
/// <param name="DownloadBytes">Downloaded byte count.</param>
/// <param name="StartedAt">Connection start time when reported; otherwise current time.</param>
/// <remarks>
/// Invariants: String values are never null; byte counts are non-negative.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct ActiveConnection(
    string Id,
    string ProcessName,
    string Host,
    string RuleName,
    string RulePayload,
    string ProxyName,
    long UploadBytes,
    long DownloadBytes,
    DateTimeOffset StartedAt)
{
    /// <summary>Gets the combined rule display text.</summary>
    /// <value>Rule name and payload display; never null.</value>
    public string RuleDisplay => MainlandChinaTextDisplayService.Instance.Apply(RawRuleDisplay);

    /// <summary>Gets the raw combined rule text.</summary>
    /// <value>Rule name and payload display before UI-only filtering; never null.</value>
    public string RawRuleDisplay => string.IsNullOrWhiteSpace(RulePayload) ? RuleName : $"{RuleName},{RulePayload}";

    /// <summary>Gets the UI-filtered process name.</summary>
    /// <value>Process name after mainland China UI replacement; never null.</value>
    public string ProcessNameDisplay => MainlandChinaTextDisplayService.Instance.Apply(ProcessName);

    /// <summary>Gets the UI-filtered host display text.</summary>
    /// <value>Host display text after mainland China UI replacement; never null.</value>
    public string HostDisplay => MainlandChinaTextDisplayService.Instance.Apply(Host);

    /// <summary>Gets the UI-filtered proxy chain display text.</summary>
    /// <value>Proxy chain display text after mainland China UI replacement; never null.</value>
    public string ProxyNameDisplay => MainlandChinaTextDisplayService.Instance.Apply(ProxyName);

    /// <summary>Gets the upload byte count formatted for UI display.</summary>
    /// <value>Formatted upload byte count; never null.</value>
    public string UploadDisplay => FormatByteCount(UploadBytes);

    /// <summary>Gets the download byte count formatted for UI display.</summary>
    /// <value>Formatted download byte count; never null.</value>
    public string DownloadDisplay => FormatByteCount(DownloadBytes);

    /// <summary>Formats a byte count for compact UI display.</summary>
    /// <param name="bytes">Byte count.</param>
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

        return value.ToString("N1", CultureInfo.CurrentCulture) + " " + units[unitIndex];
    }
}
