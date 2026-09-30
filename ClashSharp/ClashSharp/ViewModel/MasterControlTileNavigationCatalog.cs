namespace ClashSharp.ViewModel;

/// <summary>The shell page and localized label offered from an information tile's details.</summary>
internal sealed record MasterControlTileNavigationTarget(string Tag, string LabelKey);

/// <summary>Connects dashboard information to the existing pages that manage or explain it.</summary>
internal static class MasterControlTileNavigationCatalog
{
    public static MasterControlTileNavigationTarget? Resolve(string id) => id switch
    {
        "active-profile" or "profile-count" or "profile-updated" or "core-config-file"
            => new("Profiles", "Nav.Profiles"),
        "subscription-count" or "subscription-usage" or "subscription-expiry"
            => new("Links", "Nav.Links"),
        "current-node" or "proxy-node-count" => new("ProxyNodes", "Nav.ProxyNodes"),
        "rule-count" => new("Rules", "Nav.Rules"),
        "trigger-count" => new("Triggers", "Nav.Triggers"),
        "active-connections" => new("Connections", "Nav.Connections"),
        "system-log-count" => new("Logs", "Statistics.LogsShortcut.Title"),
        "connection-records" or "traffic-total" or "traffic-snapshots" or "node-health-records"
            => new("Statistics", "Nav.Statistics"),
        "app-version" or "system-info" => new("About", "Nav.About"),
        "core" or "core-owner" or "proxy-address" or "system-proxy" or "current-mode"
            or "tray-status" or "tray-visible-features" or "tray-monochrome-icon" or "close-behavior"
            or "startup-behavior" or "app-theme" or "display-language" or "sampling-interval"
            or "app-accent" or "mainland-feature-mode" or "startup-restore-fallback" or "mihomo-service"
            or "mixed-port" or "proxy-test-timeout" or "proxy-test-url" or "log-storage-limit"
            or "notification-level"
            => new("Settings", "Nav.Settings"),
        _ => null,
    };
}
