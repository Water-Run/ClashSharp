/*
 * Mihomo Profile Shape Validator
 * Validates minimum top-level mihomo profile sections before import
 *
 * @author: WaterRun
 * @file: Service/MihomoProfileShapeValidator.cs
 * @date: 2026-06-17
 */

using System;

namespace ClashSharp.Service;

/// <summary>Validates minimum top-level mihomo profile sections before import.</summary>
/// <remarks>
/// Invariants: Validation is a conservative shape check and does not prove full YAML or mihomo correctness.
/// Thread safety: Stateless methods are safe for concurrent calls.
/// Side effects: None.
/// </remarks>
internal static class MihomoProfileShapeValidator
{
    /// <summary>Validates that a profile contains the minimum mihomo sections required for import.</summary>
    /// <param name="configurationText">Configuration text. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> is null.</exception>
    /// <exception cref="ArgumentException">The configuration does not look like a mihomo profile.</exception>
    public static void Validate(string configurationText)
    {
        ArgumentNullException.ThrowIfNull(configurationText);

        bool hasProxySource = ContainsTopLevelKey(configurationText, "proxies")
            || ContainsTopLevelKey(configurationText, "proxy-providers");
        bool hasProxyGroups = ContainsTopLevelKey(configurationText, "proxy-groups");
        bool hasRules = ContainsTopLevelKey(configurationText, "rules");

        if (!hasProxySource || !hasProxyGroups || !hasRules)
        {
            throw new ArgumentException("Downloaded configuration must contain proxies or proxy-providers, proxy-groups, and rules sections.", nameof(configurationText));
        }
    }

    /// <summary>Returns whether <paramref name="configurationText"/> contains a top-level YAML key.</summary>
    /// <param name="configurationText">Configuration text. Must not be null.</param>
    /// <param name="key">Top-level key without the colon. Must not be null.</param>
    /// <returns>True when the key is present at the start of a line; otherwise false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> or <paramref name="key"/> is null.</exception>
    private static bool ContainsTopLevelKey(string configurationText, string key)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        ArgumentNullException.ThrowIfNull(key);

        string prefix = key + ":";
        foreach (string line in configurationText.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
