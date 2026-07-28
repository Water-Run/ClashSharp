using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Mihomo service control contract required by transparent proxy settings.</summary>
internal interface IMihomoServiceController
{
    /// <summary>Gets current service status.</summary>
    /// <returns>Current service status.</returns>
    MihomoServiceStatus GetLatestStatus();

    /// <summary>Refreshes current service status asynchronously.</summary>
    Task<MihomoServiceStatus> RefreshStatusAsync(CancellationToken cancellationToken);

    /// <summary>Deploys the mihomo Windows service.</summary>
    /// <param name="cancellationToken">Cancels deployment wait when requested.</param>
    /// <returns>Updated service status.</returns>
    Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken);

    /// <summary>Uninstalls the mihomo Windows service.</summary>
    /// <param name="cancellationToken">Cancels uninstall wait when requested.</param>
    /// <returns>Updated service status.</returns>
    Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken);
}
