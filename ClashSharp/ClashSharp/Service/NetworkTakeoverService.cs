/*
 * Network Takeover Service
 * Applies Clash# master takeover modes to mihomo runtime state and Windows system proxy settings
 *
 * @author: WaterRun
 * @file: Service/NetworkTakeoverService.cs
 * @date: 2026-06-15
 */

using System;
using System.ComponentModel;
using System.IO;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Applies Clash# master takeover modes to the local core process and Windows system proxy.</summary>
/// <remarks>
/// Invariants: Disabled and standby modes leave Windows system proxy disabled; rule and full takeover modes enable it.
/// Thread safety: Public mode application is serialized through a private lock.
/// Side effects: Starts or stops mihomo and mutates Windows system proxy state.
/// </remarks>
public sealed class NetworkTakeoverService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="NetworkTakeoverService"/> instance.</value>
    public static NetworkTakeoverService Instance { get; } = new();

    /// <summary>Synchronization object guarding runtime mode transitions for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Initializes a new network takeover service instance.</summary>
    private NetworkTakeoverService()
    {
    }

    /// <summary>Applies <paramref name="mode"/> to the core process and Windows system proxy.</summary>
    /// <param name="mode">Selected master takeover mode to apply.</param>
    /// <returns>A <see cref="NetworkTakeoverResult"/> describing the applied runtime state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a supported runtime mode.</exception>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    public NetworkTakeoverResult ApplyMode(ClashSharpMode mode)
    {
        lock (_syncLock)
        {
            return mode switch
            {
                ClashSharpMode.Disabled => ApplyDisabledMode(),
                ClashSharpMode.Standby => ApplyStandbyMode(),
                ClashSharpMode.RuleTakeover => ApplySystemProxyTakeoverMode(mode, "Rule takeover is active through Windows system proxy."),
                ClashSharpMode.FullTakeover => ApplySystemProxyTakeoverMode(mode, "Full takeover is active through Windows system proxy."),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Clash# runtime mode."),
            };
        }
    }

    /// <summary>Applies disabled mode by stopping the core and disabling Windows proxy.</summary>
    /// <returns>The resulting disabled-mode takeover state.</returns>
    private static NetworkTakeoverResult ApplyDisabledMode()
    {
        MihomoCoreService.Instance.Stop();
        WindowsProxyService.Instance.DisableProxy();
        return new NetworkTakeoverResult(ClashSharpMode.Disabled, false, false, "Clash# is disabled and Windows system proxy is off.");
    }

    /// <summary>Applies standby mode by starting the core and disabling Windows proxy.</summary>
    /// <returns>The resulting standby-mode takeover state.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    private static NetworkTakeoverResult ApplyStandbyMode()
    {
        RestartCore(ClashSharpMode.Standby);
        WindowsProxyService.Instance.DisableProxy();
        return new NetworkTakeoverResult(ClashSharpMode.Standby, true, false, "Clash# core is running in standby with Windows system proxy off.");
    }

    /// <summary>Applies a takeover mode by starting the core and enabling Windows proxy.</summary>
    /// <param name="mode">Takeover mode that should enable Windows proxy.</param>
    /// <param name="message">Human-readable outcome message. Must not be null.</param>
    /// <returns>The resulting takeover state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    private static NetworkTakeoverResult ApplySystemProxyTakeoverMode(ClashSharpMode mode, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        RestartCore(mode);
        int mixedPort = AppSettingsService.Instance.MixedPort;
        string proxyServer = ProxyRecoveryService.Instance.BuildLoopbackProxyServer(mixedPort);
        WindowsProxyService.Instance.EnableProxy(proxyServer);
        return new NetworkTakeoverResult(mode, true, true, message);
    }

    /// <summary>Ensures the managed core configuration matches <paramref name="mode"/> and restarts the core process.</summary>
    /// <param name="mode">Master takeover mode whose equivalent core mode should be active.</param>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">The core process cannot be started.</exception>
    private static void RestartCore(ClashSharpMode mode)
    {
        CoreConfigurationState configurationState = CoreConfigurationService.Instance.EnsureConfiguration(mode);
        MihomoCoreService.Instance.Restart(configurationState);
    }
}
