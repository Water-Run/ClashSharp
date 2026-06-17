/*
 * Mihomo Profile Preview Parser Tests
 * Verifies conservative mihomo profile preview extraction for proxy nodes, providers, and rules
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/MihomoProfilePreviewParserTests.cs
 * @date: 2026-06-17
 */

using System;
using System.Linq;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests pure mihomo profile preview parsing behavior.</summary>
/// <remarks>
/// Invariants: Tests parse in-memory profile text only and never start mihomo or read user profile files.
/// Thread safety: xUnit may run tests concurrently; parser methods are stateless.
/// Side effects: None.
/// </remarks>
public sealed class MihomoProfilePreviewParserTests
{
    /// <summary>Verifies block-list proxy nodes are parsed with protocol, host, port, and inferred region metadata.</summary>
    [Fact]
    public void ParseNodes_BlockProxyList_ReturnsNodePreviewRows()
    {
        const string Configuration = """
            proxies:
              - name: HK Primary
                type: ss
                server: hk.example.invalid
                port: 443
              - name: Broken Port
                type: vmess
                server: us.example.invalid
                port: 70000
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - HK Primary
            rules:
              - MATCH,GLOBAL
            """;

        ProxyNode[] nodes = MihomoProfilePreviewParser.ParseNodes(Configuration, ResolveRegion).ToArray();

        Assert.Equal(2, nodes.Length);
        Assert.Equal("HK Primary", nodes[0].Name);
        Assert.Equal("SS", nodes[0].Protocol);
        Assert.Equal("hk.example.invalid", nodes[0].ServerHost);
        Assert.Equal(443, nodes[0].ServerPort);
        Assert.Equal("HK", nodes[0].Region.RegionCode);
        Assert.Equal("Broken Port", nodes[1].Name);
        Assert.Null(nodes[1].ServerPort);
        Assert.Equal("US", nodes[1].Region.RegionCode);
    }

    /// <summary>Verifies provider maps are represented as provider preview rows.</summary>
    [Fact]
    public void ParseNodes_ProviderMap_ReturnsProviderPreviewRows()
    {
        const string Configuration = """
            proxy-providers:
              airport:
                type: http
                url: https://sg.example.invalid/sub.yaml
              fallback: { type: file, path: ./fallback.yaml }
            rules:
              - MATCH,DIRECT
            """;

        ProxyNode[] nodes = MihomoProfilePreviewParser.ParseNodes(Configuration, ResolveRegion).ToArray();

        Assert.Equal(2, nodes.Length);
        Assert.Equal("airport", nodes[0].Name);
        Assert.Equal("PROVIDER/HTTP", nodes[0].Protocol);
        Assert.Equal("sg.example.invalid", nodes[0].ServerHost);
        Assert.Equal("SG", nodes[0].Region.RegionCode);
        Assert.Equal("fallback", nodes[1].Name);
        Assert.Equal("PROVIDER/FILE", nodes[1].Protocol);
        Assert.Equal(string.Empty, nodes[1].ServerHost);
    }

    /// <summary>Verifies optional mihomo rule flags do not replace the routing action.</summary>
    [Fact]
    public void ParseRules_RuleWithNoResolveFlag_UsesActionBeforeFlag()
    {
        const string Configuration = """
            rules:
              - GEOIP,CN,DIRECT,no-resolve
              - DOMAIN-SUFFIX,example.com,GLOBAL
              - MATCH,DIRECT
            """;

        RulePreview[] rules = MihomoProfilePreviewParser.ParseRules(Configuration).ToArray();

        Assert.Equal(3, rules.Length);
        Assert.Equal("GEOIP", rules[0].RuleType);
        Assert.Equal("CN", rules[0].Payload);
        Assert.Equal("DIRECT", rules[0].Action);
        Assert.Equal("DOMAIN-SUFFIX", rules[1].RuleType);
        Assert.Equal("example.com", rules[1].Payload);
        Assert.Equal("GLOBAL", rules[1].Action);
        Assert.Equal("MATCH", rules[2].RuleType);
        Assert.Equal("*", rules[2].Payload);
        Assert.Equal("DIRECT", rules[2].Action);
    }

    /// <summary>Resolves test region metadata without reading user display settings.</summary>
    /// <param name="regionCode">Region code emitted by the parser. Must not be null.</param>
    /// <returns>Deterministic test metadata for the region code.</returns>
    private static RegionMetadata ResolveRegion(string regionCode)
    {
        ArgumentNullException.ThrowIfNull(regionCode);
        return new RegionMetadata(regionCode, regionCode, regionCode);
    }
}
