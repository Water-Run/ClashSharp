/*
 * Proxy Recovery Service
 * Provides stale Windows proxy detection and explicit recovery actions after abnormal exits
 *
 * @author: WaterRun
 * @file: Service/ProxyRecoveryService.cs
 * @date: 2026-06-15
 */

using System;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides settings required by startup stale-proxy recovery.</summary>
internal interface IProxyRecoverySettings
{
    /// <summary>Gets whether stale Windows proxy state should be checked on startup.</summary>
    bool CheckStaleProxyOnStartup { get; }

    /// <summary>Gets the configured Clash# mixed proxy port.</summary>
    int MixedPort { get; }

}

/// <summary>Reads and mutates Windows system proxy state for stale-proxy recovery.</summary>
internal interface IProxyRecoveryWindowsProxy
{
    /// <summary>Returns the current Windows proxy state.</summary>
    WindowsProxyState GetCurrentState();

    /// <summary>Disables Windows system proxy.</summary>
    void DisableProxy();
}

/// <summary>Detects and recovers stale Windows proxy settings that point to the Clash# local proxy port.</summary>
/// <remarks>
/// Invariants: Recovery actions only run when stale proxy detection matches the configured mixed port.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: Recovery may mutate Windows system proxy settings.
/// </remarks>
public sealed partial class ProxyRecoveryService
{
    private readonly IProxyRecoverySettings _settings;

    private readonly IProxyRecoveryWindowsProxy _windowsProxy;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes a new proxy recovery service instance.</summary>
    internal ProxyRecoveryService(
        IProxyRecoverySettings settings,
        IProxyRecoveryWindowsProxy windowsProxy,
        Func<string, string> getString)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
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

    /// <summary>Disables stale Clash# Windows proxy state when detected at startup.</summary>
    /// <returns>A <see cref="ProxyRecoveryResult"/> describing whether recovery changed Windows proxy state.</returns>
    /// <exception cref="InvalidOperationException">Windows proxy state cannot be read or written.</exception>
    public ProxyRecoveryResult ApplyStartupRecoveryIfNeeded()
    {
        if (!_settings.CheckStaleProxyOnStartup)
        {
            return new ProxyRecoveryResult(false, GetString("ProxyRecovery.CheckDisabled"));
        }

        WindowsProxyState state = _windowsProxy.GetCurrentState();
        if (!IsStaleClashProxy(state, _settings.MixedPort))
        {
            return new ProxyRecoveryResult(false, GetString("ProxyRecovery.NoStaleProxy"));
        }

        _windowsProxy.DisableProxy();
        return new ProxyRecoveryResult(true, GetString("ProxyRecovery.Disabled"));
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

    private string GetString(string key)
    {
        return _getString(key);
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
