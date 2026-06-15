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

/// <summary>Detects and recovers stale Windows proxy settings that point to the Clash# local proxy port.</summary>
/// <remarks>
/// Invariants: Recovery actions only run when stale proxy detection matches the configured mixed port.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: Recovery may mutate Windows system proxy settings through <see cref="WindowsProxyService"/>.
/// </remarks>
public sealed class ProxyRecoveryService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProxyRecoveryService"/> instance.</value>
    public static ProxyRecoveryService Instance { get; } = new();

    /// <summary>Initializes a new proxy recovery service instance.</summary>
    private ProxyRecoveryService()
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

        return ContainsLoopbackHost(state.ProxyServer) && state.ProxyServer.Contains($":{mixedPort}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies the configured startup recovery policy when the current proxy state is stale.</summary>
    /// <returns>A <see cref="ProxyRecoveryResult"/> describing whether recovery changed Windows proxy state.</returns>
    /// <exception cref="InvalidOperationException">Windows proxy state cannot be read or written.</exception>
    public ProxyRecoveryResult ApplyStartupRecoveryIfNeeded()
    {
        AppSettingsService settings = AppSettingsService.Instance;
        if (!settings.CheckStaleProxyOnStartup)
        {
            return new ProxyRecoveryResult(false, "Startup stale-proxy check is disabled.");
        }

        WindowsProxyState state = WindowsProxyService.Instance.GetCurrentState();
        if (!IsStaleClashProxy(state, settings.MixedPort))
        {
            return new ProxyRecoveryResult(false, "No stale Clash# proxy state was detected.");
        }

        switch (settings.ProxyRecoveryMode)
        {
            case ProxyRecoveryMode.EnableProxy:
                WindowsProxyService.Instance.EnableProxy(BuildLoopbackProxyServer(settings.MixedPort));
                return new ProxyRecoveryResult(true, "Windows proxy was restored to the Clash# proxy endpoint.");
            case ProxyRecoveryMode.DisableProxy:
                WindowsProxyService.Instance.DisableProxy();
                return new ProxyRecoveryResult(true, "Windows proxy was disabled because stale Clash# proxy state was detected.");
            default:
                return new ProxyRecoveryResult(false, "Stale Clash# proxy state was detected, but recovery policy is set to do nothing.");
        }
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

    /// <summary>Determines whether <paramref name="proxyServer"/> contains a loopback host token.</summary>
    /// <param name="proxyServer">Windows proxy server string. Must not be null.</param>
    /// <returns>True when a loopback host token is present; otherwise false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="proxyServer"/> is null.</exception>
    private static bool ContainsLoopbackHost(string proxyServer)
    {
        ArgumentNullException.ThrowIfNull(proxyServer);

        return proxyServer.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || proxyServer.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || proxyServer.Contains("[::1]", StringComparison.OrdinalIgnoreCase);
    }
}
