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
    /// <summary>Recognizes localized labels and flags without overriding a more specific named location.</summary>
    [Theory]
    [InlineData("中国香港 01", "HK")]
    [InlineData("香港IPLC", "HK")]
    [InlineData("🇭🇰 节点 01", "HK")]
    [InlineData("中國澳門 02", "MO")]
    [InlineData("🇲🇴 Node", "MO")]
    [InlineData("🇨🇳 中国台湾 01", "TW")]
    [InlineData("中國臺灣 02", "TW")]
    [InlineData("台灣 03", "TW")]
    [InlineData("🇹🇼 Node", "TW")]
    [InlineData("日本专线", "JP")]
    [InlineData("🇯🇵 Node", "JP")]
    [InlineData("韓國 01", "KR")]
    [InlineData("🇰🇷 Node", "KR")]
    [InlineData("新加坡Premium", "SG")]
    [InlineData("🇸🇬 Node", "SG")]
    [InlineData("美国 01", "US")]
    [InlineData("🇺🇸 Node", "US")]
    [InlineData("英國 01", "GB")]
    [InlineData("🇬🇧 Node", "GB")]
    [InlineData("德国 01", "DE")]
    [InlineData("🇩🇪 Node", "DE")]
    [InlineData("法國 01", "FR")]
    [InlineData("🇫🇷 Node", "FR")]
    [InlineData("中国内地 01", "CN")]
    [InlineData("中國大陸 02", "CN")]
    [InlineData("🇨🇳 Node", "CN")]
    [InlineData("🇺🇸 Hong Kong 01", "HK")]
    [InlineData("🇭🇰 Japan 01", "JP")]
    public void ParseNodes_LocalizedRegionHints_ReturnExpectedRegion(string name, string expectedRegion)
    {
        string configuration = $"proxies:\n  - {{name: '{name}', type: trojan, server: edge.example.invalid, port: 443}}";

        ProxyNode node = Assert.Single(MihomoProfilePreviewParser.ParseNodes(configuration, ResolveRegion));

        Assert.Equal(expectedRegion, node.Region.RegionCode);
        Assert.Equal(name, node.Name);
    }

    /// <summary>Accepts a sequence marker on its own line, as emitted by common subscription serializers.</summary>
    [Theory]
    [InlineData("", "\n")]
    [InlineData("  ", "\n")]
    [InlineData("", "\r\n")]
    [InlineData("  ", "\r\n")]
    public void ParseNodes_StandaloneSequenceMarkers_ReturnsEveryNode(string indentation, string newline)
    {
        string configuration = string.Join(newline,
            "proxies:",
            indentation + "-",
            indentation + "  name: HK Primary",
            indentation + "  type: trojan",
            indentation + "  server: hk.example.invalid",
            indentation + "  port: 443",
            indentation + "-",
            indentation + "  name: JP Secondary",
            indentation + "  type: ss",
            indentation + "  server: jp.example.invalid",
            indentation + "  port: 8443",
            "proxy-groups:",
            "  - name: GLOBAL",
            "    type: select",
            "    proxies: [HK Primary, JP Secondary]",
            "rules:",
            "  - MATCH,GLOBAL");

        ProxyNode[] nodes = MihomoProfilePreviewParser.ParseNodes(configuration, ResolveRegion).ToArray();

        Assert.Equal(["HK Primary", "JP Secondary"], nodes.Select(node => node.Name));
        Assert.Equal(["TROJAN", "SS"], nodes.Select(node => node.Protocol));
        Assert.Equal([443, 8443], nodes.Select(node => node.ServerPort));
        Assert.Equal(["HK", "JP"], nodes.Select(node => node.Region.RegionCode));
        Assert.Single(MihomoProfilePreviewParser.ParseRules(configuration, key => key));
    }

    /// <summary>Verifies English words do not accidentally imply a country, while common labels still work.</summary>
    [Theory]
    [InlineData("Long node - 完整结果可读性 - long-name", "UN")]
    [InlineData("remote", "UN")]
    [InlineData("network", "UN")]
    [InlineData("business", "UN")]
    [InlineData("fragment", "UN")]
    [InlineData("[DE] Frankfurt 01", "DE")]
    [InlineData("HK01", "HK")]
    [InlineData("US-02", "US")]
    [InlineData("tokyo primary", "JP")]
    [InlineData("node de", "DE")]
    public void ParseNodes_RegionHints_RequireWordBoundaries(string name, string expectedRegion)
    {
        string configuration = $"proxies:\n  - {{name: '{name}', type: http, server: 127.0.0.1, port: 8080}}";

        ProxyNode node = Assert.Single(MihomoProfilePreviewParser.ParseNodes(configuration, ResolveRegion));

        Assert.Equal(expectedRegion, node.Region.RegionCode);
    }

    /// <summary>Verifies a named region takes priority over a conflicting host hint.</summary>
    [Fact]
    public void ParseNodes_NamedRegion_TakesPriorityOverHost()
    {
        const string Configuration = "proxies:\n  - {name: US-01, type: http, server: hk.example.invalid, port: 8080}";

        ProxyNode node = Assert.Single(MihomoProfilePreviewParser.ParseNodes(Configuration, ResolveRegion));

        Assert.Equal("US", node.Region.RegionCode);
    }

    /// <summary>Verifies subscription paths and query values do not invent a provider region.</summary>
    [Fact]
    public void ParseNodes_ProviderUrlPath_DoesNotInferRegion()
    {
        const string Configuration = "proxy-providers:\n  remote: {type: http, url: 'https://example.invalid/us/list?region=hk'}";

        ProxyNode node = Assert.Single(MihomoProfilePreviewParser.ParseNodes(Configuration, ResolveRegion));

        Assert.Equal("UN", node.Region.RegionCode);
    }

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

        RulePreview[] rules = MihomoProfilePreviewParser.ParseRules(Configuration, key => key).ToArray();

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

    /// <summary>Verifies rule preview source names are resolved through an injected localizer.</summary>
    [Fact]
    public void ParseRules_WithLocalization_UsesInjectedSourceName()
    {
        const string Configuration = """
            rules:
              - DOMAIN-SUFFIX,example.com,PROXY
            """;

        RulePreview rule = Assert.Single(MihomoProfilePreviewParser.ParseRules(
            Configuration,
            key => key == "ProfilePreview.CurrentConfiguration" ? "localized profile" : key));

        Assert.Equal("localized profile", rule.ProviderName);
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
