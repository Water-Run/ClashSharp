/*
 * Tray Menu State Builder
 * Builds deterministic system tray menu state for Clash# controls
 *
 * @author: WaterRun
 * @file: Service/TrayMenuStateBuilder.cs
 * @date: 2026-06-24
 */

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
/// <param name="ModeItems">Mode submenu items.</param>
/// <param name="TransparentProxyItem">Transparent proxy menu item.</param>
/// <param name="SettingsLabel">Settings command label.</param>
/// <param name="SafeExitLabel">Safe exit command label.</param>
public readonly record struct TrayMenuState(
    IReadOnlyList<TrayModeMenuItem> ModeItems,
    TrayCheckMenuItem TransparentProxyItem,
    string SettingsLabel,
    string SafeExitLabel);

/// <summary>Builds deterministic tray menu state from runtime settings.</summary>
public static class TrayMenuStateBuilder
{
    /// <summary>Builds tray menu state.</summary>
    /// <param name="currentMode">Currently active Clash# mode.</param>
    /// <param name="transparentProxyEnabled">True when transparent proxy preference is enabled.</param>
    /// <param name="mihomoServiceInstalled">True when the mihomo service is deployed.</param>
    /// <returns>Tray menu state.</returns>
    public static TrayMenuState Build(
        ClashSharpMode currentMode,
        bool transparentProxyEnabled,
        bool mihomoServiceInstalled)
    {
        return new TrayMenuState(
            [
                new(ClashSharpMode.Disabled, "未启用", currentMode == ClashSharpMode.Disabled),
                new(ClashSharpMode.Standby, "待命", currentMode == ClashSharpMode.Standby),
                new(ClashSharpMode.RuleTakeover, "按规则接管", currentMode == ClashSharpMode.RuleTakeover),
                new(ClashSharpMode.FullTakeover, "接管所有", currentMode == ClashSharpMode.FullTakeover),
            ],
            new TrayCheckMenuItem("透明代理", transparentProxyEnabled && mihomoServiceInstalled, mihomoServiceInstalled),
            "设置",
            "安全退出");
    }
}
