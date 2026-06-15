/*
 * Log Record Model
 * Represents one SQLite-backed application log entry for the logs page
 *
 * @author: WaterRun
 * @file: Model/LogRecord.cs
 * @date: 2026-06-15
 */

using System;
using System.Globalization;

namespace ClashSharp.Model;

/// <summary>Represents one SQLite-backed application log entry for display.</summary>
/// <param name="CreatedAt">Timestamp when the log record was created.</param>
/// <param name="Level">Log severity level; never null.</param>
/// <param name="Source">Log source component; never null.</param>
/// <param name="Message">Primary log message; never null.</param>
/// <param name="Detail">Optional detail text; never null but may be empty.</param>
/// <remarks>
/// Invariants: String values are never null.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct LogRecord(
    DateTimeOffset CreatedAt,
    string Level,
    string Source,
    string Message,
    string Detail)
{
    /// <summary>Gets the display timestamp for this log entry.</summary>
    /// <value>Local time formatted with the current culture; never null.</value>
    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>Gets the combined source and message display text.</summary>
    /// <value>Human-readable source-prefixed message text; never null.</value>
    public string SummaryDisplay => $"[{Source}] {Message}";
}
