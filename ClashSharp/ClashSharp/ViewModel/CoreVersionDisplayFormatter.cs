/*
 * Core Version Display Formatter
 * Normalizes mihomo version probe output for compact UI display
 *
 * @author: WaterRun
 * @file: ViewModel/CoreVersionDisplayFormatter.cs
 * @date: 2026-06-24
 */

#nullable enable

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClashSharp.ViewModel;

/// <summary>Formats raw core version output into a stable display token.</summary>
/// <remarks>
/// Invariants: Non-empty input returns a non-empty display string.
/// Thread safety: Stateless and thread-safe.
/// Side effects: None.
/// </remarks>
internal static partial class CoreVersionDisplayFormatter
{
    /// <summary>Extracts a semantic version token from raw core output when one is present.</summary>
    /// <param name="rawText">Raw version probe output. Must not be null.</param>
    /// <returns>Stable version token or the first non-empty output line when no version token is present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rawText"/> is null.</exception>
    public static string Format(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        string firstLine = rawText
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        Match match = VersionPattern().Match(firstLine);

        return match.Success ? match.Value : firstLine;
    }

    /// <summary>Gets the compiled pattern used to find semantic version tokens.</summary>
    /// <returns>Regex matching optional-v semantic versions.</returns>
    [GeneratedRegex(@"(?i)\bv?\d+\.\d+\.\d+(?:[-+][0-9a-z.-]+)?\b")]
    private static partial Regex VersionPattern();
}
