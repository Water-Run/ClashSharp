using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Network;

/// <summary>Identifies why a network state transition is requested.</summary>
public enum NetworkIntentKind
{
    /// <summary>A user, tray command, trigger, or startup behavior changes the takeover mode.</summary>
    ModeTransition,

    /// <summary>Startup recovery disables stale ClashSharp-owned Windows proxy state.</summary>
    StartupProxyRecovery,

    /// <summary>An explicit user repair disables the currently observed Windows manual proxy.</summary>
    ProxyConflictRepair,

    /// <summary>Shutdown applies the configured verified network exit policy.</summary>
    Shutdown,
}

/// <summary>Describes one desired network transition without performing side effects.</summary>
/// <param name="Kind">Reason and policy category for the transition.</param>
/// <param name="Mode">Desired takeover mode.</param>
/// <param name="TransparentProxyEnabled">Whether TUN transparent proxy is desired.</param>
/// <param name="MixedPort">Desired mixed HTTP/SOCKS port.</param>
public sealed record NetworkIntent(
    NetworkIntentKind Kind,
    ClashSharpMode Mode,
    bool TransparentProxyEnabled,
    int MixedPort)
{
    /// <summary>Creates a validated takeover-mode transition.</summary>
    /// <param name="mode">Desired supported takeover mode.</param>
    /// <param name="transparentProxyEnabled">Whether TUN transparent proxy is desired.</param>
    /// <param name="mixedPort">Desired mixed port in the inclusive range 1 through 65535.</param>
    /// <returns>A validated mode-transition intent.</returns>
    public static NetworkIntent ChangeMode(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort)
    {
        ValidateMode(mode);
        ValidatePort(mixedPort);
        return new NetworkIntent(NetworkIntentKind.ModeTransition, mode, transparentProxyEnabled, mixedPort);
    }

    /// <summary>Creates a validated startup stale-proxy recovery intent.</summary>
    public static NetworkIntent RecoverStartupProxy(
        ClashSharpMode currentMode,
        bool transparentProxyEnabled,
        int mixedPort)
    {
        ValidateMode(currentMode);
        ValidatePort(mixedPort);
        return new NetworkIntent(
            NetworkIntentKind.StartupProxyRecovery,
            currentMode,
            transparentProxyEnabled,
            mixedPort);
    }

    /// <summary>Creates a validated explicit Windows proxy conflict-repair intent.</summary>
    public static NetworkIntent DisableConflictingProxy(
        ClashSharpMode currentMode,
        bool transparentProxyEnabled,
        int mixedPort)
    {
        ValidateMode(currentMode);
        ValidatePort(mixedPort);
        return new NetworkIntent(
            NetworkIntentKind.ProxyConflictRepair,
            currentMode,
            transparentProxyEnabled,
            mixedPort);
    }

    internal static void Validate(NetworkIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateMode(intent.Mode);
        ValidatePort(intent.MixedPort);
    }

    private static void ValidateMode(ClashSharpMode mode)
    {
        if (!Enum.IsDefined(mode) || mode == ClashSharpMode.Faulted)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "A network intent requires a supported desired mode.");
        }
    }

    private static void ValidatePort(int mixedPort)
    {
        if (mixedPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(mixedPort), mixedPort, "The mixed port must be in the range 1 through 65535.");
        }
    }
}
