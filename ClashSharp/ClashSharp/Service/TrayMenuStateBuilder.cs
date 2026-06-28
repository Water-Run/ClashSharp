/*
 * Tray Menu State Builder
 * Builds deterministic system tray menu state for Clash# controls
 *
 * @author: WaterRun
 * @file: Service/TrayMenuStateBuilder.cs
 * @date: 2026-06-24
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Runtime status displayed in the tray status submenu.</summary>
/// <param name="CurrentNodeName">Current proxy node name; empty when unavailable.</param>
/// <param name="LatencyMilliseconds">Measured latency in milliseconds; null when unavailable.</param>
public readonly record struct TrayStatusSnapshot(string CurrentNodeName, int? LatencyMilliseconds)
{
    /// <summary>Unavailable status snapshot.</summary>
    public static TrayStatusSnapshot Unavailable { get; } = new(string.Empty, null);

    /// <summary>Gets whether the snapshot contains a current node name.</summary>
    public bool HasCurrentNode => !string.IsNullOrWhiteSpace(CurrentNodeName);
}

/// <summary>One tray status menu item.</summary>
/// <param name="Label">Display label; never null.</param>
/// <param name="IsEnabled">True when the status item can be clicked.</param>
public readonly record struct TrayStatusMenuItem(string Label, bool IsEnabled);

/// <summary>One tray mode menu item.</summary>
/// <param name="Mode">Clash# mode represented by the item.</param>
/// <param name="Label">Display label; never null.</param>
/// <param name="IsChecked">True when the mode is active.</param>
public readonly record struct TrayModeMenuItem(ClashSharpMode Mode, string Label, bool IsChecked);

/// <summary>One checkable tray command item.</summary>
/// <param name="Label">Display label; never null.</param>
/// <param name="IsChecked">True when the command state is active.</param>
/// <param name="IsEnabled">True when the command can be clicked.</param>
public readonly record struct TrayCheckMenuItem(string Label, bool IsChecked, bool IsEnabled);

/// <summary>One page navigation tray menu item.</summary>
/// <param name="Tag">Navigation tag used by the shell.</param>
/// <param name="Label">Display label; never null.</param>
public readonly record struct TrayPageMenuItem(string Tag, string Label);

/// <summary>Complete tray menu state.</summary>
/// <param name="StatusMenuLabel">Status submenu label.</param>
/// <param name="StatusItems">Runtime status items.</param>
/// <param name="ModeMenuLabel">Mode submenu label.</param>
/// <param name="ModeItems">Mode submenu items.</param>
/// <param name="PagesMenuLabel">Pages submenu label.</param>
/// <param name="PageItems">Page navigation items.</param>
/// <param name="TransparentProxyItem">Transparent proxy menu item.</param>
/// <param name="SettingsLabel">Settings command label.</param>
/// <param name="SafeExitLabel">Safe exit command label.</param>
public readonly record struct TrayMenuState(
    string StatusMenuLabel,
    IReadOnlyList<TrayStatusMenuItem> StatusItems,
    string ModeMenuLabel,
    IReadOnlyList<TrayModeMenuItem> ModeItems,
    string PagesMenuLabel,
    IReadOnlyList<TrayPageMenuItem> PageItems,
    TrayCheckMenuItem TransparentProxyItem,
    string SettingsLabel,
    string SafeExitLabel,
    IReadOnlySet<string> VisibleFeatureIds)
{
    public bool ShowStatus => VisibleFeatureIds.Contains("status");

    public bool ShowMode => VisibleFeatureIds.Contains("mode");

    public bool ShowPages => VisibleFeatureIds.Contains("pages");

    public bool ShowTransparentProxy => VisibleFeatureIds.Contains("transparent-proxy");

    public bool ShowSettings => VisibleFeatureIds.Contains("settings");

    public bool ShowSafeExit => VisibleFeatureIds.Contains("safe-exit");
}

/// <summary>Builds deterministic tray menu state from runtime settings.</summary>
public static class TrayMenuStateBuilder
{
    /// <summary>Builds tray menu state with localized labels.</summary>
    /// <param name="currentMode">Currently active Clash# mode.</param>
    /// <param name="transparentProxyEnabled">True when transparent proxy preference is enabled.</param>
    /// <param name="mihomoServiceInstalled">True when the mihomo service is deployed.</param>
    /// <param name="getString">Localization lookup. Must not be null.</param>
    /// <returns>Tray menu state.</returns>
    public static TrayMenuState Build(
        ClashSharpMode currentMode,
        bool transparentProxyEnabled,
        bool mihomoServiceInstalled,
        Func<string, string> getString)
    {
        return Build(
            currentMode,
            transparentProxyEnabled,
            mihomoServiceInstalled,
            TrayStatusSnapshot.Unavailable,
            null,
            getString);
    }

    /// <summary>Builds tray menu state with localized labels and runtime status.</summary>
    /// <param name="currentMode">Currently active Clash# mode.</param>
    /// <param name="transparentProxyEnabled">True when transparent proxy preference is enabled.</param>
    /// <param name="mihomoServiceInstalled">True when the mihomo service is deployed.</param>
    /// <param name="status">Current runtime status snapshot.</param>
    /// <param name="getString">Localization lookup. Must not be null.</param>
    /// <returns>Tray menu state.</returns>
    public static TrayMenuState Build(
        ClashSharpMode currentMode,
        bool transparentProxyEnabled,
        bool mihomoServiceInstalled,
        TrayStatusSnapshot status,
        Func<string, string> getString)
    {
        return Build(currentMode, transparentProxyEnabled, mihomoServiceInstalled, status, null, getString);
    }

    /// <summary>Builds tray menu state with localized labels, runtime status, and visible feature filtering.</summary>
    public static TrayMenuState Build(
        ClashSharpMode currentMode,
        bool transparentProxyEnabled,
        bool mihomoServiceInstalled,
        TrayStatusSnapshot status,
        IEnumerable<string>? visibleFeatureIds,
        Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        string modeLabel = GetModeLabel(currentMode, getString);

        return new TrayMenuState(
            getString("Tray.Menu.Status"),
            BuildStatusItems(modeLabel, status, getString),
            getString("Tray.Menu.Mode"),
            [
                new(ClashSharpMode.Disabled, GetModeLabel(ClashSharpMode.Disabled, getString), currentMode == ClashSharpMode.Disabled),
                new(ClashSharpMode.Standby, GetModeLabel(ClashSharpMode.Standby, getString), currentMode == ClashSharpMode.Standby),
                new(ClashSharpMode.RuleTakeover, GetModeLabel(ClashSharpMode.RuleTakeover, getString), currentMode == ClashSharpMode.RuleTakeover),
                new(ClashSharpMode.FullTakeover, GetModeLabel(ClashSharpMode.FullTakeover, getString), currentMode == ClashSharpMode.FullTakeover),
            ],
            getString("Tray.Menu.Pages"),
            BuildPageItems(getString),
            new TrayCheckMenuItem(getString("Settings.TransparentProxy.Title"), transparentProxyEnabled, true),
            getString("Tray.Settings"),
            getString("Tray.SafeExit"),
            BuildVisibleFeatureSet(visibleFeatureIds));
    }

    private static IReadOnlyList<TrayStatusMenuItem> BuildStatusItems(
        string modeLabel,
        TrayStatusSnapshot status,
        Func<string, string> getString)
    {
        string nodeLabel = status.HasCurrentNode
            ? string.Format(CultureInfo.CurrentCulture, getString("Tray.Status.Node.Format"), status.CurrentNodeName)
            : getString("Tray.Status.NodeUnavailable");
        string latencyLabel = status.LatencyMilliseconds is int latencyMilliseconds
            ? string.Format(CultureInfo.CurrentCulture, getString("Tray.Status.Latency.Format"), latencyMilliseconds)
            : getString("Tray.Status.LatencyUnavailable");

        return
        [
            new(string.Format(CultureInfo.CurrentCulture, getString("Tray.Status.Mode.Format"), modeLabel), false),
            new(nodeLabel, false),
            new(latencyLabel, false),
        ];
    }

    private static string GetModeLabel(ClashSharpMode mode, Func<string, string> getString)
    {
        return mode switch
        {
            ClashSharpMode.Standby => getString("Master.Mode.Standby.Title"),
            ClashSharpMode.RuleTakeover => getString("Master.Mode.RuleTakeover.Title"),
            ClashSharpMode.FullTakeover => getString("Master.Mode.FullTakeover.Title"),
            _ => getString("Master.Mode.Disabled.Title"),
        };
    }

    private static IReadOnlyList<TrayPageMenuItem> BuildPageItems(Func<string, string> getString)
    {
        return
        [
            new("MasterControl", getString("Nav.MasterControl")),
            new("ProxyNodes", getString("Nav.ProxyNodes")),
            new("Profiles", getString("Nav.Profiles")),
            new("Links", getString("Nav.Links")),
            new("Rules", getString("Nav.Rules")),
            new("Triggers", getString("Nav.Triggers")),
            new("Statistics", getString("Nav.Statistics")),
            new("Logs", getString("Nav.Logs")),
            new("About", getString("Nav.About")),
            new("Settings", getString("Nav.Settings")),
        ];
    }

    private static IReadOnlySet<string> BuildVisibleFeatureSet(IEnumerable<string>? visibleFeatureIds)
    {
        HashSet<string> features = new(StringComparer.OrdinalIgnoreCase);
        foreach (string featureId in visibleFeatureIds ?? ["status", "mode", "pages", "transparent-proxy", "settings", "safe-exit"])
        {
            if (!string.IsNullOrWhiteSpace(featureId))
            {
                features.Add(featureId.Trim());
            }
        }

        return features.Count == 0
            ? new HashSet<string>(["status", "mode", "pages", "transparent-proxy", "settings", "safe-exit"], StringComparer.OrdinalIgnoreCase)
            : features;
    }
}
