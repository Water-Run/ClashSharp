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
    private static readonly IReadOnlyDictionary<string, (string Key, string[]? Tiles)> BuiltIns =
        new Dictionary<string, (string Key, string[]? Tiles)>(StringComparer.Ordinal)
        {
            ["default"] = ("Default", null),
            ["extended"] = ("Extended",
            [
                "upload-rate", "download-rate", "session-traffic",
                "current-node", "latency", "active-connections",
                "active-profile", "subscription-usage", "subscription-expiry",
                "system-proxy", "transparent-proxy", "connection-test",
                "traffic-total", "connection-records", "public-ip",
                "core", "core-uptime", "core-memory",
                "profile-updated", "proxy-node-count", "rule-count",
                "mihomo-service", "startup-conflicts", "app-update",
            ]),
            ["minimal"] = ("Minimal",
            [
                "current-node", "latency", "active-profile",
                "upload-rate", "download-rate", "subscription-usage",
            ]),
            ["technical"] = ("Technical",
            [
                "core", "core-owner", "mihomo-service",
                "mihomo-version", "core-uptime", "core-memory",
                "system-proxy", "transparent-proxy", "proxy-address",
                "dns-mode", "dns-listen", "dns-ipv6",
                "dns-upstream", "dns-fallback", "dns-bootstrap",
                "connection-test", "public-ip", "latency",
                "active-connections", "startup-conflicts", "system-info",
            ]),
            ["general"] = ("General",
            [
                "current-node", "latency", "connection-test",
                "upload-rate", "download-rate", "traffic-total",
                "active-profile", "subscription-usage", "subscription-expiry",
                "current-mode", "startup-launch", "app-update",
            ]),
            ["complete"] = ("Complete", null),

            // Previously offered layouts still resolve in customized saved lists.
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
        };

    /// <summary>Resolves a saved template list; unknown built-in references from newer builds are skipped.</summary>
    /// <param name="getString">Localization lookup.</param>
    /// <param name="recommended">Recommended layout used by the default and legacy daily templates.</param>
    /// <param name="availableTileIds">All dashboard tiles in definition order.</param>
    /// <param name="entries">Saved list in display order.</param>
    public static IReadOnlyList<MasterInfoTilePreset> Create(
        Func<string, string> getString,
        IReadOnlyList<string> recommended,
        IReadOnlyList<string> availableTileIds,
        IReadOnlyList<MasterInfoTilePresetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(getString);
        ArgumentNullException.ThrowIfNull(recommended);
        ArgumentNullException.ThrowIfNull(availableTileIds);
        ArgumentNullException.ThrowIfNull(entries);
        List<MasterInfoTilePreset> presets = [];
        foreach (MasterInfoTilePresetEntry entry in entries)
        {
            if (entry.IsBuiltIn)
            {
                if (!BuiltIns.TryGetValue(entry.BuiltInId!, out (string Key, string[]? Tiles) builtIn)) { continue; }
                IReadOnlyList<string> tileIds = entry.BuiltInId == "complete" ? availableTileIds : builtIn.Tiles ?? recommended;
                presets.Add(new(entry.BuiltInId!, getString($"Master.Preset.{builtIn.Key}.Title"),
                    getString($"Master.Preset.{builtIn.Key}.Description"),
                    Array.AsReadOnly(tileIds.ToArray()), entry));
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
