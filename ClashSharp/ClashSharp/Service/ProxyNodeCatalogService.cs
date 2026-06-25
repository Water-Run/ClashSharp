/*
 * Proxy Node Catalog Service
 * Provides proxy node data from the active profile with a built-in direct fallback row
 *
 * @author: WaterRun
 * @file: Service/ProxyNodeCatalogService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Parses proxy node rows from the active profile.</summary>
internal interface IProxyNodeCatalogProfileNodes
{
    /// <summary>Parses proxy nodes from the active imported profile.</summary>
    IReadOnlyList<ProxyNode> ParseActiveProfileNodes();
}

/// <summary>Provides proxy node list data for the proxy nodes page.</summary>
/// <remarks>
/// Invariants: Fallback data is generated through injected region resolution so display policy is respected.
/// Thread safety: Stateless service and safe for concurrent calls when dependencies are safe.
/// Side effects: Reads active profile data and regional display policy through injected dependencies.
/// </remarks>
public sealed partial class ProxyNodeCatalogService
{
    private readonly IProxyNodeCatalogProfileNodes _profileNodes;

    private readonly Func<string, RegionMetadata> _resolveRegion;

    /// <summary>Initializes a new proxy node catalog service instance.</summary>
    internal ProxyNodeCatalogService(
        IProxyNodeCatalogProfileNodes profileNodes,
        Func<string, RegionMetadata> resolveRegion)
    {
        _profileNodes = profileNodes ?? throw new ArgumentNullException(nameof(profileNodes));
        _resolveRegion = resolveRegion ?? throw new ArgumentNullException(nameof(resolveRegion));
    }

    /// <summary>Returns nodes from the active imported profile or a direct fallback row when none are available.</summary>
    /// <returns>A read-only list of proxy nodes.</returns>
    public IReadOnlyList<ProxyNode> GetNodes()
    {
        IReadOnlyList<ProxyNode> parsedNodes = _profileNodes.ParseActiveProfileNodes();
        if (parsedNodes.Count > 0)
        {
            return parsedNodes;
        }

        return
        [
            new ProxyNode("Direct", "DIRECT", _resolveRegion("CN"), 0),
        ];
    }
}
