using System.Threading;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides the blocking system probes needed by <see cref="StartupCheckService"/>.</summary>
/// <remarks>
/// Implementations must not access XAML state. Each method is invoked at most once for one
/// <see cref="StartupCheckService.GetChecksAsync"/> operation.
/// </remarks>
internal interface IStartupCheckProbe
{
    /// <summary>Returns whether at least one subscription link is configured.</summary>
    bool HasSubscription(CancellationToken cancellationToken);

    /// <summary>Returns whether transparent proxy is enabled in application settings.</summary>
    bool IsTransparentProxyEnabled(CancellationToken cancellationToken);

    /// <summary>Returns the latest cached transparent-proxy service status.</summary>
    MihomoServiceStatus GetMihomoStatus(CancellationToken cancellationToken);

    /// <summary>Returns whether the startup restore fallback is registered.</summary>
    bool IsFallbackRegistered(CancellationToken cancellationToken);

    /// <summary>Reads the current Windows manual-proxy state.</summary>
    WindowsProxyState GetWindowsProxyState(CancellationToken cancellationToken);

    /// <summary>Returns the configured local mixed-proxy port.</summary>
    int GetMixedPort(CancellationToken cancellationToken);

    /// <summary>Returns whether the supplied Windows state points to the local proxy port.</summary>
    bool IsStaleProxy(
        WindowsProxyState state,
        int mixedPort,
        CancellationToken cancellationToken);
}
