using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

/// <summary>One localized, ordered starting point for the dashboard layout editor.</summary>
/// <param name="Id">Stable identity: the built-in identifier, or <c>custom:</c> followed by the template name.</param>
/// <param name="Title">Localized built-in title or the custom template name.</param>
/// <param name="Description">Localized explanation shown as a tooltip.</param>
/// <param name="TileIds">Ordered tile identifiers applied by the template.</param>
/// <param name="Entry">Persisted list entry this template came from, or <see langword="null"/> for a transient built-in.</param>
internal sealed record MasterInfoTilePreset(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> TileIds,
    MasterInfoTilePresetEntry? Entry = null)
{
    /// <summary>Gets whether the template is one of the built-in layouts.</summary>
    public bool IsBuiltIn => Entry is null || Entry.IsBuiltIn;
}

/// <summary>Resolves built-in layouts and saved custom templates; selecting one does not write preferences.</summary>
internal static class MasterInfoTilePresetCatalog
{
    // Each built-in fills whole rows of the three-column dashboard and avoids repeating the overview card.
    private static readonly IReadOnlyDictionary<string, (string Key, string[]? Tiles)> BuiltIns =
        new Dictionary<string, (string Key, string[]? Tiles)>(StringComparer.Ordinal)
        {
            ["daily"] = ("Daily", null),
            ["traffic"] = ("Traffic",
            [
                "upload-rate", "download-rate", "session-traffic",
                "traffic-total", "active-connections", "connection-records",
                "current-node", "latency", "core-memory",
            ]),
            ["subscriptions"] = ("Subscriptions",
            [
                "active-profile", "subscription-usage", "subscription-expiry",
                "profile-updated", "subscription-count", "profile-count",
                "proxy-node-count", "rule-count", "app-update",
            ]),
            ["diagnostics"] = ("Diagnostics",
            [
                "core-owner", "mihomo-service", "mihomo-version",
                "system-proxy", "transparent-proxy", "proxy-address",
                "dns-mode", "dns-upstream", "dns-fallback",
                "connection-test", "public-ip", "latency",
                "startup-conflicts", "core-uptime", "system-info",
            ]),
            ["minimal"] = ("Minimal",
            [
                "current-node", "latency", "active-profile",
                "upload-rate", "download-rate", "subscription-usage",
            ]),
        };

    /// <summary>Creates every built-in layout in its default order.</summary>
    /// <param name="getString">Localization lookup.</param>
    /// <param name="recommended">Recommended layout used by the daily template.</param>
    public static IReadOnlyList<MasterInfoTilePreset> Create(Func<string, string> getString, IReadOnlyList<string> recommended) =>
        Create(getString, recommended, MasterInfoTilePresetList.BuiltInIds.Select(MasterInfoTilePresetEntry.BuiltIn).ToArray());

    /// <summary>Resolves a saved template list; unknown built-in references from newer builds are skipped.</summary>
    /// <param name="getString">Localization lookup.</param>
    /// <param name="recommended">Recommended layout used by the daily template.</param>
    /// <param name="entries">Saved list in display order.</param>
    public static IReadOnlyList<MasterInfoTilePreset> Create(
        Func<string, string> getString,
        IReadOnlyList<string> recommended,
        IReadOnlyList<MasterInfoTilePresetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(getString);
        ArgumentNullException.ThrowIfNull(recommended);
        ArgumentNullException.ThrowIfNull(entries);
        List<MasterInfoTilePreset> presets = [];
        foreach (MasterInfoTilePresetEntry entry in entries)
        {
            if (entry.IsBuiltIn)
            {
                if (!BuiltIns.TryGetValue(entry.BuiltInId!, out (string Key, string[]? Tiles) builtIn)) { continue; }
                presets.Add(new(entry.BuiltInId!, getString($"Master.Preset.{builtIn.Key}.Title"),
                    getString($"Master.Preset.{builtIn.Key}.Description"),
                    Array.AsReadOnly((builtIn.Tiles ?? recommended).ToArray()), entry));
            }
            else
            {
                presets.Add(new("custom:" + entry.Name, entry.Name!,
                    string.Format(CultureInfo.CurrentCulture, getString("Master.Preset.CustomTemplate.Format"), entry.TileIds.Count),
                    entry.TileIds, entry));
            }
        }

        return presets.AsReadOnly();
    }
}
