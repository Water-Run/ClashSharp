using System;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Detects stale Windows proxy settings that point to the Clash# local proxy port.</summary>
/// <remarks>
/// Invariants: Recovery actions only run when stale proxy detection matches the configured mixed port.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: None.
/// </remarks>
public sealed partial class ProxyRecoveryService
{
    internal ProxyRecoveryService()
    {
    }

    /// <summary>Determines whether <paramref name="state"/> appears to be a stale Clash# system proxy.</summary>
    /// <param name="state">Current Windows proxy state snapshot.</param>
    /// <param name="mixedPort">Configured Clash# mixed proxy port in range [1, 65535].</param>
    /// <returns>True when Windows proxy is enabled and points to a loopback address on <paramref name="mixedPort"/>; otherwise false.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mixedPort"/> is outside the valid TCP port range.</exception>
    public bool IsStaleClashProxy(WindowsProxyState state, int mixedPort)
    {
        if (mixedPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(mixedPort), "Port must be in the range [1, 65535].");
        }

        if (!state.IsEnabled || string.IsNullOrWhiteSpace(state.ProxyServer))
        {
            return false;
        }

        return WindowsProxyEndpointMatcher.ContainsLoopbackEndpointWithPort(state.ProxyServer, mixedPort);
    }

    /// <summary>Builds the Windows proxy server string for the configured loopback mixed port.</summary>
    /// <param name="mixedPort">Configured Clash# mixed proxy port in range [1, 65535].</param>
    /// <returns>Proxy server string in host:port format.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mixedPort"/> is outside the valid TCP port range.</exception>
    public string BuildLoopbackProxyServer(int mixedPort)
    {
        if (mixedPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(mixedPort), "Port must be in the range [1, 65535].");
        }

        return $"127.0.0.1:{mixedPort}";
    }

}

/// <summary>Parses Windows manual proxy endpoint strings for loopback port ownership checks.</summary>
internal static class WindowsProxyEndpointMatcher
{
    public static bool ContainsLoopbackEndpointWithPort(string proxyServer, int port)
    {
        ArgumentNullException.ThrowIfNull(proxyServer);

        foreach (string token in proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string endpoint = ExtractEndpoint(token);
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? absoluteUri)
                && IsLoopbackHost(absoluteUri.Host)
                && absoluteUri.Port == port)
            {
                return true;
            }

            if (Uri.TryCreate("http://" + endpoint, UriKind.Absolute, out Uri? impliedUri)
                && IsLoopbackHost(impliedUri.Host)
                && impliedUri.Port == port)
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractEndpoint(string token)
    {
        int equalsIndex = token.IndexOf('=', StringComparison.Ordinal);
        return equalsIndex >= 0 ? token[(equalsIndex + 1)..].Trim() : token.Trim();
    }

    private static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }
}
