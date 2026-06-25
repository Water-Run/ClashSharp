/*
 * Proxy Node Catalog Service Factory
 * Wires production dependencies for proxy node catalog data
 *
 * @author: WaterRun
 * @file: Service/ProxyNodeCatalogServiceFactory.cs
 * @date: 2026-06-25
 */

using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class ProxyNodeCatalogService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProxyNodeCatalogService"/> instance.</value>
    public static ProxyNodeCatalogService Instance { get; } = ProxyNodeCatalogServiceFactory.CreateDefault();
}

/// <summary>Creates proxy node catalog service instances with production dependencies.</summary>
internal static class ProxyNodeCatalogServiceFactory
{
    /// <summary>Creates the default proxy node catalog service.</summary>
    /// <returns>A service wired to active profile parsing and region display policy.</returns>
    public static ProxyNodeCatalogService CreateDefault()
    {
        return new ProxyNodeCatalogService(
            new ProxyNodeCatalogProfileNodesAdapter(MihomoProfileParserService.Instance),
            RegionDisplayService.Instance.Resolve);
    }
}

internal sealed class ProxyNodeCatalogProfileNodesAdapter(MihomoProfileParserService parser) : IProxyNodeCatalogProfileNodes
{
    public IReadOnlyList<ProxyNode> ParseActiveProfileNodes()
    {
        return parser.ParseActiveProfileNodes();
    }
}
