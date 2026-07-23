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
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Ensures the mihomo runtime configuration matches the desired takeover mode.</summary>
internal interface INetworkTakeoverCoreConfiguration
{
    /// <summary>Ensures runtime configuration for <paramref name="mode"/> and transparent proxy state.</summary>
    CoreConfigurationState EnsureConfiguration(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort);
}

/// <summary>Controls the owned mihomo core process.</summary>
internal interface INetworkTakeoverCore
{
    /// <summary>Restarts the core with <paramref name="configurationState"/>.</summary>
    void Restart(CoreConfigurationState configurationState);

    /// <summary>Stops the owned core process.</summary>
    void Stop();
}

/// <summary>Mutates Windows system proxy state.</summary>
internal interface INetworkTakeoverWindowsProxy
{
    /// <summary>Disables Windows system proxy.</summary>
    void DisableProxy();

    /// <summary>Enables Windows system proxy for <paramref name="proxyServer"/>.</summary>
    void EnableProxy(string proxyServer);
}

/// <summary>Reads the installed mihomo service state required by transparent proxy.</summary>
internal interface INetworkTakeoverMihomoService
{
    /// <summary>Returns the current mihomo service status.</summary>
    Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>Builds loopback system proxy endpoints.</summary>
internal interface INetworkTakeoverProxyRecovery
{
    /// <summary>Builds the loopback proxy server string for <paramref name="mixedPort"/>.</summary>
    string BuildLoopbackProxyServer(int mixedPort);
}

/// <summary>Applies Clash# master takeover modes to the local core process and Windows system proxy.</summary>
/// <remarks>
/// Invariants: Disabled and standby modes leave Windows system proxy disabled; rule and full takeover modes prefer TUN when enabled and otherwise enable system proxy.
/// Thread safety: Public mode application is serialized through an asynchronous gate.
/// Side effects: Starts or stops mihomo and mutates Windows system proxy state through injected dependencies.
/// </remarks>
public sealed partial class NetworkTakeoverService
{
    /// <summary>Synchronization object guarding runtime mode transitions for this service lifetime.</summary>
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    private readonly INetworkTakeoverCoreConfiguration _configuration;

    private readonly INetworkTakeoverCore _core;

    private readonly INetworkTakeoverWindowsProxy _windowsProxy;

    private readonly INetworkTakeoverMihomoService _mihomoService;

    private readonly INetworkTakeoverProxyRecovery _proxyRecovery;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes a new network takeover service instance.</summary>
    internal NetworkTakeoverService(
        INetworkTakeoverCoreConfiguration configuration,
        INetworkTakeoverCore core,
        INetworkTakeoverWindowsProxy windowsProxy,
        INetworkTakeoverMihomoService mihomoService,
        INetworkTakeoverProxyRecovery proxyRecovery,
        Func<string, string> getString)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _mihomoService = mihomoService ?? throw new ArgumentNullException(nameof(mihomoService));
        _proxyRecovery = proxyRecovery ?? throw new ArgumentNullException(nameof(proxyRecovery));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Applies one immutable planned mode without rereading mutable TUN or port settings.</summary>
    internal async Task<NetworkTakeoverResult> ApplyModeAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        if (mixedPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(mixedPort), "Port must be in the range [1, 65535].");
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return mode switch
            {
                ClashSharpMode.Disabled => ApplyDisabledMode(),
                ClashSharpMode.Standby => ApplyStandbyMode(mixedPort),
                ClashSharpMode.RuleTakeover => await ApplyTakeoverModeAsync(
                    mode,
                    transparentProxyEnabled,
                    mixedPort,
                    cancellationToken).ConfigureAwait(false),
                ClashSharpMode.FullTakeover => await ApplyTakeoverModeAsync(
                    mode,
                    transparentProxyEnabled,
                    mixedPort,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Clash# runtime mode."),
            };
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Applies disabled mode by stopping the core and disabling Windows proxy.</summary>
    /// <returns>The resulting disabled-mode takeover state.</returns>
    private NetworkTakeoverResult ApplyDisabledMode()
    {
        _core.Stop();
        _windowsProxy.DisableProxy();
        return new NetworkTakeoverResult(ClashSharpMode.Disabled, false, false, false, GetString("NetworkTakeover.Disabled"));
    }

    /// <summary>Applies standby mode by starting the core and disabling Windows proxy.</summary>
    /// <returns>The resulting standby-mode takeover state.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    private NetworkTakeoverResult ApplyStandbyMode(int mixedPort)
    {
        RestartCore(ClashSharpMode.Standby, transparentProxyEnabled: false, mixedPort);
        _windowsProxy.DisableProxy();
        return new NetworkTakeoverResult(ClashSharpMode.Standby, true, false, false, GetString("NetworkTakeover.Standby"));
    }

    /// <summary>Applies a takeover mode through TUN when enabled, otherwise through Windows system proxy.</summary>
    /// <param name="mode">Takeover mode that should route traffic through mihomo.</param>
    /// <returns>The resulting takeover state.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    private async Task<NetworkTakeoverResult> ApplyTakeoverModeAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        if (!transparentProxyEnabled)
        {
            return ApplySystemProxyTakeoverMode(mode, mixedPort, BuildSystemProxyMessage(mode));
        }

        MihomoServiceStatus serviceStatus = await _mihomoService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!serviceStatus.IsInstalled || !serviceStatus.IsRunning)
        {
            return ApplySystemProxyTakeoverMode(
                mode,
                mixedPort,
                BuildTransparentProxyServiceMissingMessage(mode));
        }

        RestartCore(mode, transparentProxyEnabled: true, mixedPort);
        _windowsProxy.DisableProxy();
        return new NetworkTakeoverResult(mode, true, false, true, BuildTransparentProxyMessage(mode));
    }

    /// <summary>Applies a takeover mode by starting the core and enabling Windows proxy.</summary>
    /// <param name="mode">Takeover mode that should enable Windows proxy.</param>
    /// <param name="message">Human-readable outcome message. Must not be null.</param>
    /// <returns>The resulting takeover state.</returns>
    private NetworkTakeoverResult ApplySystemProxyTakeoverMode(
        ClashSharpMode mode,
        int mixedPort,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        RestartCore(mode, transparentProxyEnabled: false, mixedPort);
        string proxyServer = _proxyRecovery.BuildLoopbackProxyServer(mixedPort);
        _windowsProxy.EnableProxy(proxyServer);
        return new NetworkTakeoverResult(mode, true, true, false, message);
    }

    /// <summary>Ensures the managed core configuration matches <paramref name="mode"/> and restarts the core process.</summary>
    /// <param name="mode">Master takeover mode whose equivalent core mode should be active.</param>
    /// <param name="transparentProxyEnabled">True to enable mihomo TUN transparent proxy configuration.</param>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">The core process cannot be started.</exception>
    private void RestartCore(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort)
    {
        CoreConfigurationState configurationState = _configuration.EnsureConfiguration(
            mode,
            transparentProxyEnabled,
            mixedPort);
        _core.Restart(configurationState);
    }

    /// <summary>Builds user-facing message for system-proxy takeover.</summary>
    private string BuildSystemProxyMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? GetString("NetworkTakeover.SystemProxy.Full")
            : GetString("NetworkTakeover.SystemProxy.Rule");
    }

    /// <summary>Builds user-facing message for transparent-proxy takeover.</summary>
    private string BuildTransparentProxyMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? GetString("NetworkTakeover.TransparentProxy.Full")
            : GetString("NetworkTakeover.TransparentProxy.Rule");
    }

    /// <summary>Builds user-facing message when transparent proxy is unavailable because the service is missing.</summary>
    private string BuildTransparentProxyServiceMissingMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? GetString("NetworkTakeover.TransparentProxyServiceMissing.Full")
            : GetString("NetworkTakeover.TransparentProxyServiceMissing.Rule");
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}
