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
using ClashSharp.Model;

namespace ClashSharp.Service;

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

/// <summary>Complete tray menu state.</summary>
/// <param name="ModeMenuLabel">Mode submenu label.</param>
/// <param name="ModeItems">Mode submenu items.</param>
/// <param name="TransparentProxyItem">Transparent proxy menu item.</param>
/// <param name="SettingsLabel">Settings command label.</param>
/// <param name="SafeExitLabel">Safe exit command label.</param>
public readonly record struct TrayMenuState(
    string ModeMenuLabel,
    IReadOnlyList<TrayModeMenuItem> ModeItems,
    TrayCheckMenuItem TransparentProxyItem,
    string SettingsLabel,
    string SafeExitLabel);

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
        ArgumentNullException.ThrowIfNull(getString);

        return new TrayMenuState(
            getString("Tray.Menu.Mode"),
            [
                new(ClashSharpMode.Disabled, getString("Master.Mode.Disabled.Title"), currentMode == ClashSharpMode.Disabled),
                new(ClashSharpMode.Standby, getString("Master.Mode.Standby.Title"), currentMode == ClashSharpMode.Standby),
                new(ClashSharpMode.RuleTakeover, getString("Master.Mode.RuleTakeover.Title"), currentMode == ClashSharpMode.RuleTakeover),
                new(ClashSharpMode.FullTakeover, getString("Master.Mode.FullTakeover.Title"), currentMode == ClashSharpMode.FullTakeover),
            ],
            new TrayCheckMenuItem(getString("Settings.TransparentProxy.Title"), transparentProxyEnabled, true),
            getString("Tray.Settings"),
            getString("Tray.SafeExit"));
    }
}
