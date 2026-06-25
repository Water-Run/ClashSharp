/*
 * Network Takeover Service Factory
 * Wires production dependencies for Clash# network takeover mode application
 *
 * @author: WaterRun
 * @file: Service/NetworkTakeoverServiceFactory.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class NetworkTakeoverService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="NetworkTakeoverService"/> instance.</value>
    public static NetworkTakeoverService Instance { get; } = NetworkTakeoverServiceFactory.CreateDefault();
}

/// <summary>Creates network takeover service instances with production dependencies.</summary>
internal static class NetworkTakeoverServiceFactory
{
    /// <summary>Creates the default service used by application UI and background startup flows.</summary>
    /// <returns>A network takeover service wired to persistent settings, mihomo, Windows proxy, and localization resources.</returns>
    public static NetworkTakeoverService CreateDefault()
    {
        return new NetworkTakeoverService(
            new NetworkTakeoverSettingsAdapter(AppSettingsService.Instance),
            new NetworkTakeoverCoreConfigurationAdapter(CoreConfigurationService.Instance),
            new NetworkTakeoverCoreAdapter(MihomoCoreService.Instance),
            new NetworkTakeoverWindowsProxyAdapter(WindowsProxyService.Instance),
            new NetworkTakeoverMihomoServiceAdapter(MihomoServiceManager.Instance),
            new NetworkTakeoverProxyRecoveryAdapter(ProxyRecoveryService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class NetworkTakeoverSettingsAdapter(AppSettingsService settings) : INetworkTakeoverSettings
{
    public bool TransparentProxyEnabled => settings.TransparentProxyEnabled;

    public int MixedPort => settings.MixedPort;
}

internal sealed class NetworkTakeoverCoreConfigurationAdapter(CoreConfigurationService configuration) : INetworkTakeoverCoreConfiguration
{
    public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled)
    {
        return configuration.EnsureConfiguration(mode, transparentProxyEnabled);
    }
}

internal sealed class NetworkTakeoverCoreAdapter(MihomoCoreService core) : INetworkTakeoverCore
{
    public void Restart(CoreConfigurationState configurationState)
    {
        core.Restart(configurationState);
    }

    public void Stop()
    {
        core.Stop();
    }
}

internal sealed class NetworkTakeoverWindowsProxyAdapter(WindowsProxyService windowsProxy) : INetworkTakeoverWindowsProxy
{
    public void DisableProxy()
    {
        windowsProxy.DisableProxy();
    }

    public void EnableProxy(string proxyServer)
    {
        windowsProxy.EnableProxy(proxyServer);
    }
}

internal sealed class NetworkTakeoverMihomoServiceAdapter(MihomoServiceManager serviceManager) : INetworkTakeoverMihomoService
{
    public MihomoServiceStatus GetStatus()
    {
        return serviceManager.GetStatus();
    }
}

internal sealed class NetworkTakeoverProxyRecoveryAdapter(ProxyRecoveryService proxyRecovery) : INetworkTakeoverProxyRecovery
{
    public string BuildLoopbackProxyServer(int mixedPort)
    {
        return proxyRecovery.BuildLoopbackProxyServer(mixedPort);
    }
}
