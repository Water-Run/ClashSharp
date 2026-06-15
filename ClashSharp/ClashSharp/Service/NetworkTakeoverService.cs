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
/// Invariants: Disabled and standby modes leave Windows system proxy disabled; rule and full takeover modes prefer TUN when enabled and otherwise enable system proxy.
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
                ClashSharpMode.RuleTakeover => ApplyTakeoverMode(mode),
                ClashSharpMode.FullTakeover => ApplyTakeoverMode(mode),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Clash# runtime mode."),
            };
        }
    }

    /// <summary>Restores a system-proxy takeover after stale Clash# proxy state is detected on startup.</summary>
    /// <returns>The resulting system-proxy takeover state.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    public NetworkTakeoverResult ApplyStartupSystemProxyRecovery()
    {
        lock (_syncLock)
        {
            return ApplySystemProxyTakeoverMode(
                ClashSharpMode.RuleTakeover,
                "Clash# core and Windows system proxy were restored after stale proxy detection.");
        }
    }

    /// <summary>Applies disabled mode by stopping the core and disabling Windows proxy.</summary>
    /// <returns>The resulting disabled-mode takeover state.</returns>
    private static NetworkTakeoverResult ApplyDisabledMode()
    {
        MihomoCoreService.Instance.Stop();
        WindowsProxyService.Instance.DisableProxy();
        return new NetworkTakeoverResult(ClashSharpMode.Disabled, false, false, false, "Clash# is disabled and Windows system proxy is off.");
    }

    /// <summary>Applies standby mode by starting the core and disabling Windows proxy.</summary>
    /// <returns>The resulting standby-mode takeover state.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    private static NetworkTakeoverResult ApplyStandbyMode()
    {
        RestartCore(ClashSharpMode.Standby, transparentProxyEnabled: false);
        WindowsProxyService.Instance.DisableProxy();
        return new NetworkTakeoverResult(ClashSharpMode.Standby, true, false, false, "Clash# core is running in standby with Windows system proxy off.");
    }

    /// <summary>Applies a takeover mode through TUN when enabled, falling back to Windows system proxy when configured.</summary>
    /// <param name="mode">Takeover mode that should route traffic through mihomo.</param>
    /// <returns>The resulting takeover state.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    private static NetworkTakeoverResult ApplyTakeoverMode(ClashSharpMode mode)
    {
        if (!AppSettingsService.Instance.TransparentProxyEnabled)
        {
            return ApplySystemProxyTakeoverMode(mode, BuildSystemProxyMessage(mode));
        }

        try
        {
            RestartCore(mode, transparentProxyEnabled: true);
            WindowsProxyService.Instance.DisableProxy();
            return new NetworkTakeoverResult(mode, true, false, true, BuildTransparentProxyMessage(mode));
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            if (!AppSettingsService.Instance.FallbackToSystemProxyWhenTunFails)
            {
                throw;
            }

            LogStorageService.Instance.AppendLog("Warning", "NetworkTakeover", "TUN takeover failed; falling back to Windows system proxy.", exception.Message);
            return ApplySystemProxyTakeoverMode(mode, BuildFallbackMessage(mode));
        }
    }

    /// <summary>Applies a takeover mode by starting the core and enabling Windows proxy.</summary>
    /// <param name="mode">Takeover mode that should enable Windows proxy.</param>
    /// <param name="message">Human-readable outcome message. Must not be null.</param>
    /// <returns>The resulting takeover state.</returns>
    private static NetworkTakeoverResult ApplySystemProxyTakeoverMode(ClashSharpMode mode, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        RestartCore(mode, transparentProxyEnabled: false);
        int mixedPort = AppSettingsService.Instance.MixedPort;
        string proxyServer = ProxyRecoveryService.Instance.BuildLoopbackProxyServer(mixedPort);
        WindowsProxyService.Instance.EnableProxy(proxyServer);
        return new NetworkTakeoverResult(mode, true, true, false, message);
    }

    /// <summary>Ensures the managed core configuration matches <paramref name="mode"/> and restarts the core process.</summary>
    /// <param name="mode">Master takeover mode whose equivalent core mode should be active.</param>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">The core process cannot be started.</exception>
    private static void RestartCore(ClashSharpMode mode, bool transparentProxyEnabled)
    {
        CoreConfigurationState configurationState = CoreConfigurationService.Instance.EnsureConfiguration(mode, transparentProxyEnabled);
        MihomoCoreService.Instance.Restart(configurationState);
    }

    /// <summary>Builds user-facing message for system-proxy takeover.</summary>
    private static string BuildSystemProxyMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? "Full takeover is active through Windows system proxy."
            : "Rule takeover is active through Windows system proxy.";
    }

    /// <summary>Builds user-facing message for transparent-proxy takeover.</summary>
    private static string BuildTransparentProxyMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? "Full takeover is active through TUN transparent proxy."
            : "Rule takeover is active through TUN transparent proxy.";
    }

    /// <summary>Builds user-facing message for fallback from TUN to system proxy.</summary>
    private static string BuildFallbackMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? "TUN failed; full takeover is active through Windows system proxy."
            : "TUN failed; rule takeover is active through Windows system proxy.";
    }
}
