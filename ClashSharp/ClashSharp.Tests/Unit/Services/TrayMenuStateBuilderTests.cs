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

    /// <summary>Verifies runtime details are grouped under the tray status submenu.</summary>
    [Fact]
    public void Build_WithStatusSnapshot_CreatesStatusMenuItems()
    {
        static string GetString(string key)
        {
            return key switch
            {
                "Tray.Menu.Status" => "Status",
                "Tray.Status.Mode.Format" => "Mode: {0}",
                "Tray.Status.Node.Format" => "Node: {0}",
                "Tray.Status.Latency.Format" => "Latency: {0} ms",
                "Tray.Status.NodeUnavailable" => "Node: unavailable",
                "Tray.Status.LatencyUnavailable" => "Latency: unavailable",
                "Master.Mode.RuleTakeover.Title" => "Rule",
                _ => key,
            };
        }

        TrayMenuState state = TrayMenuStateBuilder.Build(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            mihomoServiceInstalled: true,
            new TrayStatusSnapshot("Proxy A", 42),
            GetString);

        Assert.Equal("Status", state.StatusMenuLabel);
        Assert.Equal(["Mode: Rule", "Node: Proxy A", "Latency: 42 ms"], state.StatusItems.Select(item => item.Label));
        Assert.All(state.StatusItems, item => Assert.False(item.IsEnabled));
    }

    /// <summary>Verifies unavailable runtime details remain explicit in the tray status submenu.</summary>
    [Fact]
    public void Build_WithoutStatusSnapshotValues_UsesUnavailableStatusText()
    {
        static string GetString(string key)
        {
            return key switch
            {
                "Tray.Menu.Status" => "Status",
                "Tray.Status.Mode.Format" => "Mode: {0}",
                "Tray.Status.Node.Format" => "Node: {0}",
                "Tray.Status.Latency.Format" => "Latency: {0} ms",
                "Tray.Status.NodeUnavailable" => "Node: unavailable",
                "Tray.Status.LatencyUnavailable" => "Latency: unavailable",
                "Master.Mode.Disabled.Title" => "Disabled",
                _ => key,
            };
        }

        TrayMenuState state = TrayMenuStateBuilder.Build(
            ClashSharpMode.Disabled,
            transparentProxyEnabled: false,
            mihomoServiceInstalled: false,
            TrayStatusSnapshot.Unavailable,
            GetString);

        Assert.Equal(["Mode: Disabled", "Node: unavailable", "Latency: unavailable"], state.StatusItems.Select(item => item.Label));
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
                "Tray.Menu.Pages" => "Pages",
                "Nav.MasterControl" => "Master",
                "Nav.ProxyNodes" => "Nodes",
                "Nav.Profiles" => "Profiles",
                "Nav.Links" => "Links",
                "Nav.Rules" => "Rules",
                "Nav.Triggers" => "Triggers",
                "Nav.Connections" => "Connections",
                "Nav.Statistics" => "Statistics",
                "Nav.Logs" => "Logs",
                "Nav.About" => "About",
                "Nav.Settings" => "Settings page",
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
        Assert.Equal("Pages", state.PagesMenuLabel);
        Assert.Equal(
            ["MasterControl", "ProxyNodes", "Profiles", "Links", "Rules", "Triggers", "Connections", "Statistics", "Logs", "About", "Settings"],
            state.PageItems.Select(item => item.Tag));
        Assert.Equal(
            ["Master", "Nodes", "Profiles", "Links", "Rules", "Triggers", "Connections", "Statistics", "Logs", "About", "Settings page"],
            state.PageItems.Select(item => item.Label));
        Assert.Equal("TUN", state.TransparentProxyItem.Label);
        Assert.Equal("Settings", state.SettingsLabel);
        Assert.Equal("Safe exit", state.SafeExitLabel);
    }

    /// <summary>Verifies tray visible feature ids filter optional menu groups without changing page items.</summary>
    [Fact]
    public void Build_WithVisibleFeatureIds_FiltersMenuFeatures()
    {
        TrayMenuState state = TrayMenuStateBuilder.Build(
            ClashSharpMode.Disabled,
            transparentProxyEnabled: false,
            mihomoServiceInstalled: true,
            TrayStatusSnapshot.Unavailable,
            ["pages", "safe-exit"],
            key => key);

        Assert.False(state.ShowStatus);
        Assert.False(state.ShowMode);
        Assert.True(state.ShowPages);
        Assert.False(state.ShowTransparentProxy);
        Assert.False(state.ShowSettings);
        Assert.True(state.ShowSafeExit);
        Assert.NotEmpty(state.PageItems);
    }

    /// <summary>Verifies a disabled color indicator preserves the fixed green brand icon.</summary>
    [Fact]
    public void ResolveIconState_WhenColorIndicatorDisabled_ReturnsDefault()
    {
        TrayMenuState state = BuildIconState(
            ClashSharpMode.FullTakeover,
            transparentProxyEnabled: true,
            runtimeKnown: true,
            systemProxyEffective: false,
            tunEffective: true);

        TrayIconVisualState result = TrayIconVisualStateResolver.Resolve(
            state,
            colorStatusIndicatorEnabled: false);

        Assert.Equal(TrayIconVisualState.Default, result);
    }

    /// <summary>Verifies disabled and standby modes share the gray inactive icon.</summary>
    [Theory]
    [InlineData(ClashSharpMode.Disabled)]
    [InlineData(ClashSharpMode.Standby)]
    public void ResolveIconState_WhenTakeoverInactive_ReturnsInactive(ClashSharpMode mode)
    {
        TrayMenuState state = BuildIconState(
            mode,
            transparentProxyEnabled: true,
            runtimeKnown: true,
            systemProxyEffective: false,
            tunEffective: true);

        TrayIconVisualState result = TrayIconVisualStateResolver.Resolve(
            state,
            colorStatusIndicatorEnabled: true);

        Assert.Equal(TrayIconVisualState.Inactive, result);
    }

    /// <summary>Verifies active system proxy takeover uses the green icon.</summary>
    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public void ResolveIconState_WhenSystemProxyActive_ReturnsSystemProxy(ClashSharpMode mode)
    {
        TrayMenuState state = BuildIconState(
            mode,
            transparentProxyEnabled: false,
            runtimeKnown: true,
            systemProxyEffective: true,
            tunEffective: false);

        TrayIconVisualState result = TrayIconVisualStateResolver.Resolve(
            state,
            colorStatusIndicatorEnabled: true);

        Assert.Equal(TrayIconVisualState.SystemProxy, result);
    }

    /// <summary>Verifies TUN takes priority and uses the C# purple icon.</summary>
    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public void ResolveIconState_WhenTunActive_ReturnsTun(ClashSharpMode mode)
    {
        TrayMenuState state = BuildIconState(
            mode,
            transparentProxyEnabled: true,
            runtimeKnown: true,
            systemProxyEffective: false,
            tunEffective: true);

        TrayIconVisualState result = TrayIconVisualStateResolver.Resolve(
            state,
            colorStatusIndicatorEnabled: true);

        Assert.Equal(TrayIconVisualState.Tun, result);
    }

    /// <summary>Verifies effective TUN wins over effective system proxy when both probes report active.</summary>
    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public void ResolveIconState_WhenTunAndSystemProxyAreActive_ReturnsTun(ClashSharpMode mode)
    {
        TrayMenuState state = BuildIconState(
            mode,
            transparentProxyEnabled: true,
            runtimeKnown: true,
            systemProxyEffective: true,
            tunEffective: true);

        TrayIconVisualState result = TrayIconVisualStateResolver.Resolve(
            state,
            colorStatusIndicatorEnabled: true);

        Assert.Equal(TrayIconVisualState.Tun, result);
    }

    /// <summary>Verifies a requested TUN mode that fell back to App ownership stays system-proxy green.</summary>
    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public void ResolveIconState_WhenTunRequestedButNotEffective_ReturnsSystemProxy(ClashSharpMode mode)
    {
        TrayMenuState state = BuildIconState(
            mode,
            transparentProxyEnabled: true,
            runtimeKnown: true,
            systemProxyEffective: true,
            tunEffective: false);

        TrayIconVisualState result = TrayIconVisualStateResolver.Resolve(
            state,
            colorStatusIndicatorEnabled: true);

        Assert.True(state.TransparentProxyItem.IsChecked);
        Assert.False(state.TunEffective);
        Assert.Equal(TrayIconVisualState.SystemProxy, result);
    }

    /// <summary>Verifies unknown or unconfirmed takeover state fails neutral to gray.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ResolveIconState_WhenRuntimeUnknownOrProxyUnconfirmed_ReturnsInactive(
        bool runtimeKnown,
        bool systemProxyEffective)
    {
        TrayMenuState state = BuildIconState(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            runtimeKnown,
            systemProxyEffective,
            tunEffective: false);

        TrayIconVisualState result = TrayIconVisualStateResolver.Resolve(
            state,
            colorStatusIndicatorEnabled: true);

        Assert.Equal(TrayIconVisualState.Inactive, result);
    }

    private static TrayMenuState BuildIconState(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        bool runtimeKnown,
        bool systemProxyEffective,
        bool tunEffective)
    {
        return TrayMenuStateBuilder.Build(
            mode,
            transparentProxyEnabled,
            mihomoServiceInstalled: true,
            runtimeKnown,
            systemProxyEffective,
            tunEffective,
            TrayStatusSnapshot.Unavailable,
            visibleFeatureIds: null,
            key => key);
    }
}
