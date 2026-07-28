using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="MihomoControllerClient"/> to runtime proxy controls.</summary>
internal sealed class ProxyRuntimeControllerAdapter : IProxyRuntimeController
{
    /// <summary>Wrapped controller client.</summary>
    private readonly MihomoControllerClient _controller;

    /// <summary>Initializes a runtime controller adapter.</summary>
    /// <param name="controller">Controller client. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="controller"/> is null.</exception>
    public ProxyRuntimeControllerAdapter(MihomoControllerClient controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    /// <summary>Gets runtime strategy groups from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>Runtime strategy groups.</returns>
    public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
    {
        return _controller.GetProxyGroupsAsync(cancellationToken);
    }

    /// <summary>Gets runtime provider resources from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>Provider resources.</returns>
    public Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken)
    {
        return _controller.GetProviderResourcesAsync(cancellationToken);
    }

    /// <summary>Selects one proxy inside a runtime strategy group.</summary>
    /// <param name="groupName">Strategy group name. Must not be null.</param>
    /// <param name="proxyName">Proxy name. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>A task that completes after mihomo applies the selection.</returns>
    public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        return _controller.SelectProxyAsync(groupName, proxyName, cancellationToken);
    }

    /// <summary>Updates one runtime provider resource.</summary>
    /// <param name="provider">Provider resource to update.</param>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>A task that completes after mihomo updates the provider.</returns>
    public Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
    {
        return _controller.UpdateProviderAsync(provider, cancellationToken);
    }
}
