/*
 * Proxy Recovery Service
 * Provides stale Windows proxy detection and explicit recovery actions after abnormal exits
 *
 * @author: WaterRun
 * @file: Service/ProxyRecoveryService.cs
 * @date: 2026-06-15
 */

using System;
using System.ComponentModel;
using System.IO;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides settings required by startup stale-proxy recovery.</summary>
internal interface IProxyRecoverySettings
{
    /// <summary>Gets whether stale Windows proxy state should be checked on startup.</summary>
    bool CheckStaleProxyOnStartup { get; }

    /// <summary>Gets the configured Clash# mixed proxy port.</summary>
    int MixedPort { get; }

    /// <summary>Gets the configured recovery action for stale proxy state.</summary>
    ProxyRecoveryMode ProxyRecoveryMode { get; }
}

/// <summary>Reads and mutates Windows system proxy state for stale-proxy recovery.</summary>
internal interface IProxyRecoveryWindowsProxy
{
    /// <summary>Returns the current Windows proxy state.</summary>
    WindowsProxyState GetCurrentState();

    /// <summary>Disables Windows system proxy.</summary>
    void DisableProxy();
}

/// <summary>Restores Clash# system-proxy takeover after stale proxy detection.</summary>
internal interface IProxyRecoveryTakeover
{
    /// <summary>Applies startup system-proxy recovery through the network takeover service.</summary>
    NetworkTakeoverResult ApplyStartupSystemProxyRecovery();
}

/// <summary>Detects and recovers stale Windows proxy settings that point to the Clash# local proxy port.</summary>
/// <remarks>
/// Invariants: Recovery actions only run when stale proxy detection matches the configured mixed port.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: Recovery may start the core process or mutate Windows system proxy settings.
/// </remarks>
public sealed partial class ProxyRecoveryService
{
    private readonly IProxyRecoverySettings _settings;

    private readonly IProxyRecoveryWindowsProxy _windowsProxy;

    private readonly IProxyRecoveryTakeover _takeover;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes a new proxy recovery service instance.</summary>
    internal ProxyRecoveryService(
        IProxyRecoverySettings settings,
        IProxyRecoveryWindowsProxy windowsProxy,
        IProxyRecoveryTakeover takeover,
        Func<string, string> getString)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
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

        return ContainsLoopbackHost(state.ProxyServer) && state.ProxyServer.Contains($":{mixedPort}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies the configured startup recovery policy when the current proxy state is stale.</summary>
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

        switch (_settings.ProxyRecoveryMode)
        {
            case ProxyRecoveryMode.EnableProxy:
                return ApplyStartupEnableProxyRecovery();
            case ProxyRecoveryMode.DisableProxy:
                _windowsProxy.DisableProxy();
                return new ProxyRecoveryResult(true, GetString("ProxyRecovery.Disabled"));
            default:
                return new ProxyRecoveryResult(false, GetString("ProxyRecovery.DoNothing"));
        }
    }

    /// <summary>Restores the core and Windows proxy to an enabled state, disabling stale proxy if restoration fails.</summary>
    /// <returns>The successful recovery result.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects a proxy change notification.</exception>
    /// <exception cref="UnauthorizedAccessException">Windows proxy settings cannot be changed by the current user.</exception>
    private ProxyRecoveryResult ApplyStartupEnableProxyRecovery()
    {
        try
        {
            NetworkTakeoverResult result = _takeover.ApplyStartupSystemProxyRecovery();
            return new ProxyRecoveryResult(true, result.Message);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            _windowsProxy.DisableProxy();
            throw;
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

    private string GetString(string key)
    {
        return _getString(key);
    }
}
