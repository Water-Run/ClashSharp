using System;
using System.Collections.Generic;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="ProxyNodeCatalogService"/> to proxy node catalog reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null catalog service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads active profile data through the wrapped service.
/// </remarks>
internal sealed class ProxyNodeCatalogAdapter : IProxyNodeCatalog
{
    /// <summary>Wrapped proxy node catalog service.</summary>
    private readonly ProxyNodeCatalogService _catalog;

    /// <summary>Initializes a proxy node catalog adapter.</summary>
    /// <param name="catalog">Proxy node catalog service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public ProxyNodeCatalogAdapter(ProxyNodeCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>Gets proxy node rows for display.</summary>
    /// <returns>Read-only proxy node list.</returns>
    public IReadOnlyList<ProxyNode> GetNodes()
    {
        return _catalog.GetNodes();
    }
}
