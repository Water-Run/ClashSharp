extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using LegacyNetworkIntentSource =
    ClashSharpUi::ClashSharp.Hosting.Compatibility.LegacyNetworkIntentSource;
using LegacyNetworkShutdownPolicy =
    ClashSharpUi::ClashSharp.Hosting.Compatibility.LegacyNetworkShutdownPolicy;

namespace ClashSharp.Tests.Unit.Hosting;

/// <summary>Verifies launch-specific policies cannot undo startup fallback cleanup.</summary>
public sealed class LegacyNetworkIntentSourceTests
{
    [Fact]
    public void CreateShutdownIntent_StartupRestoreFallbackForcesDisabledFinalState()
    {
        NetworkIntent intent = LegacyNetworkIntentSource.CreateShutdownIntent(
            ClashSharpMode.RuleTakeover,
            restoreProxyOnExit: false,
            transparentProxyEnabled: true,
            mixedPort: 7890,
            LegacyNetworkShutdownPolicy.StartupRestoreFallback);

        Assert.Equal(NetworkIntentKind.Shutdown, intent.Kind);
        Assert.Equal(ClashSharpMode.Disabled, intent.Mode);
        Assert.False(intent.TransparentProxyEnabled);
        Assert.Equal(7890, intent.MixedPort);
    }

    [Fact]
    public void CreateShutdownIntent_NormalExitStillHonorsConfiguredCurrentMode()
    {
        NetworkIntent intent = LegacyNetworkIntentSource.CreateShutdownIntent(
            ClashSharpMode.FullTakeover,
            restoreProxyOnExit: false,
            transparentProxyEnabled: true,
            mixedPort: 7890,
            LegacyNetworkShutdownPolicy.Configured);

        Assert.Equal(NetworkIntentKind.Shutdown, intent.Kind);
        Assert.Equal(ClashSharpMode.FullTakeover, intent.Mode);
        Assert.True(intent.TransparentProxyEnabled);
    }
}
