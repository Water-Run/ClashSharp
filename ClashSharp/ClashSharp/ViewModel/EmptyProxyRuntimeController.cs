using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Fallback runtime controller used by legacy constructors.</summary>
internal sealed class EmptyProxyRuntimeController : IProxyRuntimeController
{
    public static EmptyProxyRuntimeController Instance { get; } = new();

    private EmptyProxyRuntimeController()
    {
    }

    public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<MihomoProxyGroup>>([]);
    }

    public Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<MihomoProviderResource>>([]);
    }

    public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
