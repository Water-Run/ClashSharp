/*
 * Mainland China Text Display Service
 * Applies mainland China UI-only text replacement without mutating stored data
 *
 * @author: WaterRun
 * @file: Service/MainlandChinaTextDisplayService.cs
 * @date: 2026-06-15
 */

using System;

namespace ClashSharp.Service;

/// <summary>Applies mainland China UI-only text replacement without mutating stored data.</summary>
/// <remarks>
/// Invariants: Replacement is applied only when the mainland China display policy is enabled.
/// Thread safety: Stateless service and safe for concurrent reads.
/// Side effects: Reads mainland China display policy from <see cref="AppSettingsService"/>.
/// </remarks>
public sealed class MainlandChinaTextDisplayService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="MainlandChinaTextDisplayService"/> instance.</value>
    public static MainlandChinaTextDisplayService Instance { get; } = new();

    /// <summary>Sensitive UI terms replaced when mainland China display is enabled.</summary>
    private static readonly string[] SensitiveTerms =
    [
        "习近平",
        "習近平",
        "64",
        "六四",
        "天安门",
        "天安門",
    ];

    /// <summary>Initializes the display service.</summary>
    private MainlandChinaTextDisplayService()
    {
    }

    /// <summary>Applies UI-only replacement to <paramref name="text"/> when enabled.</summary>
    /// <param name="text">Input display text. Must not be null.</param>
    /// <returns>Display text with sensitive terms replaced when the policy is enabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!AppSettingsService.Instance.MainlandChinaDisplayEnabled)
        {
            return text;
        }

        string filteredText = text;
        foreach (string term in SensitiveTerms)
        {
            filteredText = filteredText.Replace(term, "***", StringComparison.OrdinalIgnoreCase);
        }

        return filteredText;
    }
}
