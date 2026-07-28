using System;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Chooses the network state that must remain after one host shutdown path.</summary>
internal enum LegacyNetworkShutdownPolicy
{
    /// <summary>Honors the user's configured normal-exit behavior.</summary>
    Configured,

    /// <summary>Leaves startup restore fallback cleanup disabled even when normal exit preserves takeover.</summary>
    StartupRestoreFallback,
}

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
        return CreateShutdownIntent(
            GetSupportedCurrentMode(),
            settings.RestoreProxyOnExit,
            settings.TransparentProxyEnabled,
            settings.MixedPort,
            LegacyNetworkShutdownPolicy.Configured);
    }

    public NetworkIntent CreateStartupRestoreFallbackShutdown()
    {
        return CreateShutdownIntent(
            GetSupportedCurrentMode(),
            settings.RestoreProxyOnExit,
            settings.TransparentProxyEnabled,
            settings.MixedPort,
            LegacyNetworkShutdownPolicy.StartupRestoreFallback);
    }

    internal static NetworkIntent CreateShutdownIntent(
        ClashSharpMode currentMode,
        bool restoreProxyOnExit,
        bool transparentProxyEnabled,
        int mixedPort,
        LegacyNetworkShutdownPolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        ClashSharpMode mode = policy == LegacyNetworkShutdownPolicy.StartupRestoreFallback
            || restoreProxyOnExit
                ? ClashSharpMode.Disabled
                : currentMode;
        bool finalTransparentProxyEnabled =
            policy == LegacyNetworkShutdownPolicy.StartupRestoreFallback
                ? false
                : transparentProxyEnabled;
        return NetworkIntent.Shutdown(mode, finalTransparentProxyEnabled, mixedPort);
    }

    private ClashSharpMode GetSupportedCurrentMode()
    {
        ClashSharpMode mode = settings.CurrentMode;
        return Enum.IsDefined(mode) && mode != ClashSharpMode.Faulted
            ? mode
            : ClashSharpMode.Disabled;
    }
}
