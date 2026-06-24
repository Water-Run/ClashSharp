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
            mihomoServiceInstalled: true);

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
            mihomoServiceInstalled: true);

        Assert.True(state.TransparentProxyItem.IsEnabled);
        Assert.True(state.TransparentProxyItem.IsChecked);
    }

    /// <summary>Verifies transparent proxy tray command is disabled when the mihomo service is not deployed.</summary>
    [Fact]
    public void Build_WhenServiceMissing_DisablesTransparentProxyCommand()
    {
        TrayMenuState state = TrayMenuStateBuilder.Build(
            ClashSharpMode.Standby,
            transparentProxyEnabled: true,
            mihomoServiceInstalled: false);

        Assert.False(state.TransparentProxyItem.IsEnabled);
        Assert.False(state.TransparentProxyItem.IsChecked);
    }
}
