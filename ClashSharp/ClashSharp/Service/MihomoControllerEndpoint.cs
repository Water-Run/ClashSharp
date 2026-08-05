using System;

namespace ClashSharp.Service;

/// <summary>Defines the Clash#-owned local mihomo controller endpoint.</summary>
/// <remarks>
/// Invariants: The controller is bound only to IPv4 loopback and every generated configuration uses authentication.
/// Thread safety: Immutable constants are safe for concurrent use.
/// Side effects: None.
/// </remarks>
internal static class MihomoControllerEndpoint
{
    /// <summary>Loopback address and port emitted into mihomo configuration.</summary>
    public const string ListenAddress = "127.0.0.1:9090";

    /// <summary>HTTP base URI used by the local controller client.</summary>
    public static Uri BaseUri { get; } = new($"http://{ListenAddress}/");

    /// <summary>Returns whether a persisted controller secret has the generated 256-bit hex shape.</summary>
    public static bool IsValidSecret(string? secret)
    {
        if (secret is not { Length: 64 })
        {
            return false;
        }

        foreach (char character in secret)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
