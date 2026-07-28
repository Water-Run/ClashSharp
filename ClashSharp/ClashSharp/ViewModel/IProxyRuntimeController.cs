using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Runtime proxy controller contract required by <see cref="ProxiesViewModel"/>.</summary>
internal interface IProxyRuntimeController
{
    Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken);

    Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken);

    Task UpdateProviderAsync(
        MihomoProviderResource provider,
        CancellationToken cancellationToken);
}
