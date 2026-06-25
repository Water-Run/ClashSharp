/*
 * Proxy Node Catalog Service Tests
 * Verifies proxy node catalog composition through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/ProxyNodeCatalogServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for proxy node catalog composition.</summary>
public sealed class ProxyNodeCatalogServiceTests
{
    /// <summary>Verifies parsed active profile nodes are returned without creating fallback rows.</summary>
    [Fact]
    public void GetNodes_WhenActiveProfileHasNodes_ReturnsParsedNodes()
    {
        ProxyNode parsedNode = new("HK", "SS", new RegionMetadata("HK", "Hong Kong", "hk.png"), 25);
        ProxyNodeCatalogService service = new(
            new FakeProxyNodeCatalogProfileNodes([parsedNode]),
            code => new RegionMetadata(code, code, code));

        IReadOnlyList<ProxyNode> nodes = service.GetNodes();

        ProxyNode node = Assert.Single(nodes);
        Assert.Equal(parsedNode, node);
    }

    /// <summary>Verifies missing active profile nodes produce a direct fallback with injected region metadata.</summary>
    [Fact]
    public void GetNodes_WhenActiveProfileHasNoNodes_ReturnsDirectFallback()
    {
        ProxyNodeCatalogService service = new(
            new FakeProxyNodeCatalogProfileNodes([]),
            code => new RegionMetadata(code, "localized " + code, "asset-" + code));

        IReadOnlyList<ProxyNode> nodes = service.GetNodes();

        ProxyNode node = Assert.Single(nodes);
        Assert.Equal("Direct", node.Name);
        Assert.Equal("DIRECT", node.Protocol);
        Assert.Equal("CN", node.Region.RegionCode);
        Assert.Equal("localized CN", node.Region.DisplayName);
        Assert.Equal(0, node.LatencyMilliseconds);
    }

    private sealed class FakeProxyNodeCatalogProfileNodes(IReadOnlyList<ProxyNode> nodes) : IProxyNodeCatalogProfileNodes
    {
        public IReadOnlyList<ProxyNode> ParseActiveProfileNodes()
        {
            return nodes;
        }
    }
}
