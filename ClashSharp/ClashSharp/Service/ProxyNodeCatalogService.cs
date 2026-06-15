/*
 * Proxy Node Catalog Service
 * Provides proxy node data from the active profile with a built-in direct fallback row
 *
 * @author: WaterRun
 * @file: Service/ProxyNodeCatalogService.cs
 * @date: 2026-06-15
 */

using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides proxy node list data for the proxy nodes page.</summary>
/// <remarks>
/// Invariants: Fallback data is generated through <see cref="RegionDisplayService"/> so display policy is respected.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: Reads the active profile file and regional display policy from settings.
/// </remarks>
public sealed class ProxyNodeCatalogService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProxyNodeCatalogService"/> instance.</value>
    public static ProxyNodeCatalogService Instance { get; } = new();

    /// <summary>Initializes a new proxy node catalog service instance.</summary>
    private ProxyNodeCatalogService()
    {
    }

    /// <summary>Returns nodes from the active imported profile or a direct fallback row when none are available.</summary>
    /// <returns>A read-only list of proxy nodes.</returns>
    public IReadOnlyList<ProxyNode> GetNodes()
    {
        IReadOnlyList<ProxyNode> parsedNodes = MihomoProfileParserService.Instance.ParseActiveProfileNodes();
        if (parsedNodes.Count > 0)
        {
            return parsedNodes;
        }

        RegionDisplayService regions = RegionDisplayService.Instance;
        return
        [
            new ProxyNode("Direct", "DIRECT", regions.Resolve("CN"), 0),
        ];
    }
}
