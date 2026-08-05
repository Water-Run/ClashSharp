using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Read-only Mihomo service status contract required by transparent proxy settings.</summary>
internal interface IMihomoServiceController
{
    /// <summary>Gets current service status.</summary>
    /// <returns>Current service status.</returns>
    MihomoServiceStatus GetLatestStatus();

    /// <summary>Refreshes current service status asynchronously.</summary>
    Task<MihomoServiceStatus> RefreshStatusAsync(CancellationToken cancellationToken);
}
