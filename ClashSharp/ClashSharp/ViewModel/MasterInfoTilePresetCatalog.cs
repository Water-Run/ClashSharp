using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashSharp.ViewModel;

/// <summary>One localized, ordered starting point for the dashboard layout editor.</summary>
internal sealed record MasterInfoTilePreset(string Id, string Title, string Description, IReadOnlyList<string> TileIds);

/// <summary>Built-in layouts only; selecting one does not write preferences.</summary>
internal static class MasterInfoTilePresetCatalog
{
    public static IReadOnlyList<MasterInfoTilePreset> Create(Func<string, string> getString, IReadOnlyList<string> recommended)
    {
        ArgumentNullException.ThrowIfNull(getString);
        ArgumentNullException.ThrowIfNull(recommended);
        MasterInfoTilePreset Preset(string id, string key, IEnumerable<string> ids) => new(
            id, getString($"Master.Preset.{key}.Title"), getString($"Master.Preset.{key}.Description"),
            Array.AsReadOnly(ids.ToArray()));
        return Array.AsReadOnly<MasterInfoTilePreset>(
        [
            Preset("daily", "Daily", recommended),
            Preset("diagnostics", "Diagnostics",
            [
                "core", "core-owner", "mihomo-version", "mihomo-service", "system-proxy", "transparent-proxy",
                "proxy-address", "current-node", "latency", "dns-mode", "dns-listen", "dns-ipv6",
                "dns-upstream", "dns-fallback", "dns-bootstrap", "connection-test", "public-ip", "rule-count",
            ]),
            Preset("subscriptions", "Subscriptions",
            [
                "active-profile", "subscription-usage", "subscription-expiry", "profile-updated", "profile-count",
                "subscription-count", "proxy-node-count", "rule-count", "current-node", "latency", "app-update",
            ]),
            Preset("minimal", "Minimal",
            [
                "current-mode", "current-node", "active-profile", "upload-rate", "download-rate", "latency",
            ]),
        ]);
    }
}
