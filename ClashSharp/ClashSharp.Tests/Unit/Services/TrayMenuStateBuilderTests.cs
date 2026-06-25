/*
 * Tray Menu State Builder Tests
 * Verifies task tray menu state without creating a native tray icon
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/TrayMenuStateBuilderTests.cs
 * @date: 2026-06-24
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for tray menu state construction.</summary>
public sealed class TrayMenuStateBuilderTests
{
    /// <summary>Verifies only the active Clash# mode is checked in the tray mode submenu.</summary>
    [Fact]
    public void Build_ChecksOnlyActiveMode()
    {
        TrayMenuState state = TrayMenuStateBuilder.Build(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            mihomoServiceInstalled: true,
            key => key);

        Assert.Equal(4, state.ModeItems.Count);
        Assert.True(state.ModeItems.Single(item => item.Mode == ClashSharpMode.RuleTakeover).IsChecked);
        Assert.All(
            state.ModeItems.Where(item => item.Mode != ClashSharpMode.RuleTakeover),
            item => Assert.False(item.IsChecked));
    }

    /// <summary>Verifies transparent proxy tray command mirrors enabled state when the service is deployed.</summary>
    [Fact]
    public void Build_WhenServiceInstalled_EnablesTransparentProxyCommand()
    {
        TrayMenuState state = TrayMenuStateBuilder.Build(
            ClashSharpMode.Standby,
            transparentProxyEnabled: true,
            mihomoServiceInstalled: true,
            key => key);

        Assert.True(state.TransparentProxyItem.IsEnabled);
        Assert.True(state.TransparentProxyItem.IsChecked);
    }

    /// <summary>Verifies transparent proxy tray command mirrors preference even when the service is not deployed.</summary>
    [Fact]
    public void Build_WhenServiceMissing_PreservesTransparentProxyPreference()
    {
        TrayMenuState state = TrayMenuStateBuilder.Build(
            ClashSharpMode.Standby,
            transparentProxyEnabled: true,
            mihomoServiceInstalled: false,
            key => key);

        Assert.True(state.TransparentProxyItem.IsEnabled);
        Assert.True(state.TransparentProxyItem.IsChecked);
    }

    /// <summary>Verifies tray menu labels are resolved through localization keys.</summary>
    [Fact]
    public void Build_WithLocalization_UsesLocalizedLabels()
    {
        static string GetString(string key)
        {
            return key switch
            {
                "Tray.Menu.Mode" => "Mode",
                "Master.Mode.Disabled.Title" => "Disabled",
                "Master.Mode.Standby.Title" => "Standby",
                "Master.Mode.RuleTakeover.Title" => "Rule",
                "Master.Mode.FullTakeover.Title" => "Global",
                "Settings.TransparentProxy.Title" => "TUN",
                "Tray.Settings" => "Settings",
                "Tray.SafeExit" => "Safe exit",
                _ => key,
            };
        }

        TrayMenuState state = TrayMenuStateBuilder.Build(
            ClashSharpMode.Disabled,
            transparentProxyEnabled: false,
            mihomoServiceInstalled: true,
            GetString);

        Assert.Equal("Mode", state.ModeMenuLabel);
        Assert.Equal(["Disabled", "Standby", "Rule", "Global"], state.ModeItems.Select(item => item.Label));
        Assert.Equal("TUN", state.TransparentProxyItem.Label);
        Assert.Equal("Settings", state.SettingsLabel);
        Assert.Equal("Safe exit", state.SafeExitLabel);
    }
}
