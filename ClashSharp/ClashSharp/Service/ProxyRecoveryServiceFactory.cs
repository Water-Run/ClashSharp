/*
 * Proxy Recovery Service Factory
 * Wires production dependencies for startup stale proxy recovery
 *
 * @author: WaterRun
 * @file: Service/ProxyRecoveryServiceFactory.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class ProxyRecoveryService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProxyRecoveryService"/> instance.</value>
    public static ProxyRecoveryService Instance { get; } = ProxyRecoveryServiceFactory.CreateDefault();
}

/// <summary>Creates proxy recovery service instances with production dependencies.</summary>
internal static class ProxyRecoveryServiceFactory
{
    /// <summary>Creates the default service used by application startup flows.</summary>
    /// <returns>A proxy recovery service wired to settings, Windows proxy, takeover, and localization resources.</returns>
    public static ProxyRecoveryService CreateDefault()
    {
        return new ProxyRecoveryService(
            new ProxyRecoverySettingsAdapter(AppSettingsService.Instance),
            new ProxyRecoveryWindowsProxyAdapter(WindowsProxyService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class ProxyRecoverySettingsAdapter(AppSettingsService settings) : IProxyRecoverySettings
{
    public bool CheckStaleProxyOnStartup => settings.CheckStaleProxyOnStartup;

    public int MixedPort => settings.MixedPort;
}

internal sealed class ProxyRecoveryWindowsProxyAdapter(WindowsProxyService windowsProxy) : IProxyRecoveryWindowsProxy
{
    public WindowsProxyState GetCurrentState()
    {
        return windowsProxy.GetCurrentState();
    }

    public void DisableProxy()
    {
        windowsProxy.DisableProxy();
    }
}
