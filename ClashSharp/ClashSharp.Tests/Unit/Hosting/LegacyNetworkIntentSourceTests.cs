extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using LegacyNetworkIntentSource =
    ClashSharpUi::ClashSharp.Hosting.Compatibility.LegacyNetworkIntentSource;

namespace ClashSharp.Tests.Unit.Hosting;

/// <summary>Verifies normal exit honors the configured network policy.</summary>
public sealed class LegacyNetworkIntentSourceTests
{
    [Fact]
    public void CreateShutdownIntent_RestoreOnExitDisablesTakeover()
    {
        NetworkIntent intent = LegacyNetworkIntentSource.CreateShutdownIntent(
            ClashSharpMode.RuleTakeover,
            restoreProxyOnExit: true,
            transparentProxyEnabled: true,
            mixedPort: 7890);

        Assert.Equal(NetworkIntentKind.Shutdown, intent.Kind);
        Assert.Equal(ClashSharpMode.Disabled, intent.Mode);
        Assert.True(intent.TransparentProxyEnabled);
        Assert.Equal(7890, intent.MixedPort);
    }

    [Fact]
    public void CreateShutdownIntent_NormalExitStillHonorsConfiguredCurrentMode()
    {
        NetworkIntent intent = LegacyNetworkIntentSource.CreateShutdownIntent(
            ClashSharpMode.FullTakeover,
            restoreProxyOnExit: false,
            transparentProxyEnabled: true,
            mixedPort: 7890);

        Assert.Equal(NetworkIntentKind.Shutdown, intent.Kind);
        Assert.Equal(ClashSharpMode.FullTakeover, intent.Mode);
        Assert.True(intent.TransparentProxyEnabled);
    }
}
