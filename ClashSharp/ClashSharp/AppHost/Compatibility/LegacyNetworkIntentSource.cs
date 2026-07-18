using System;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Reads legacy settings once to construct validated application-layer network intents.</summary>
internal sealed class LegacyNetworkIntentSource(AppSettingsService settings)
{
    public NetworkIntent CreateModeTransition(ClashSharpMode mode)
    {
        return NetworkIntent.ChangeMode(mode, settings.TransparentProxyEnabled, settings.MixedPort);
    }

    public NetworkIntent CreateStartupRecovery()
    {
        return NetworkIntent.RecoverStartupProxy(
            GetSupportedCurrentMode(),
            settings.TransparentProxyEnabled,
            settings.MixedPort);
    }

    public NetworkIntent CreateStartupBehavior()
    {
        ClashSharpMode mode = StartupBehaviorService.ResolveStartupMode(
            settings.StartupBehaviorMode,
            GetSupportedCurrentMode());
        return CreateModeTransition(mode);
    }

    public NetworkIntent CreateShutdown()
    {
        ClashSharpMode mode = settings.RestoreProxyOnExit
            ? ClashSharpMode.Disabled
            : GetSupportedCurrentMode();
        return NetworkIntent.Shutdown(
            mode,
            settings.TransparentProxyEnabled,
            settings.MixedPort);
    }

    private ClashSharpMode GetSupportedCurrentMode()
    {
        ClashSharpMode mode = settings.CurrentMode;
        return Enum.IsDefined(mode) && mode != ClashSharpMode.Faulted
            ? mode
            : ClashSharpMode.Disabled;
    }
}
