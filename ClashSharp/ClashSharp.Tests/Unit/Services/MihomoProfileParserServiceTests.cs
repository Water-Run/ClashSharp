using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for the mihomo profile parser service.</summary>
public sealed class MihomoProfileParserServiceTests
{
    [Theory]
    [InlineData("AND,((NETWORK,UDP),(DST-PORT,443)),REJECT", "((NETWORK,UDP),(DST-PORT,443))", "REJECT")]
    [InlineData("SUB-RULE,(NETWORK,TCP),sub1", "(NETWORK,TCP)", "sub1")]
    [InlineData("DOMAIN-REGEX,foo{1,3}\\.example,PROXY,no-resolve", "foo{1,3}\\.example", "PROXY")]
    [InlineData("PROCESS-PATH,C:\\apps\\tools(legacy\\app.exe,DIRECT", "C:\\apps\\tools(legacy\\app.exe", "DIRECT")]
    public void ComplexRulePreviewPreservesTheCompletePayload(string raw, string payload, string action)
    {
        RulePreview rule = Assert.Single(CreateService(new("rules:\n  - '" + raw + "'\n")).ParseActiveProfileRules());
        Assert.Equal(payload, rule.Payload);
        Assert.Equal(action, rule.Action);
    }

    /// <summary>Verifies active-profile node parsing reads text from the injected source and uses injected region metadata.</summary>
    [Fact]
    public void ParseActiveProfileNodes_WhenProfileTextExists_ParsesNodesWithInjectedRegionResolver()
    {
        FakeMihomoProfileTextSource textSource = new("""
            proxies:
              - name: HK Primary
                type: ss
                server: hk.example.invalid
                port: 443
            """);
        MihomoProfileParserService service = CreateService(textSource);

        ProxyNode node = Assert.Single(service.ParseActiveProfileNodes());

        Assert.Equal("HK Primary", node.Name);
        Assert.Equal("SS", node.Protocol);
        Assert.Equal("HK", node.Region.RegionCode);
        Assert.Equal("region HK", node.Region.DisplayName);
        Assert.Equal(1, textSource.ReadCount);
    }

    /// <summary>Verifies active-profile rule parsing returns an empty set without file IO when no text is available.</summary>
    [Fact]
    public void ParseActiveProfileRules_WhenProfileTextMissing_ReturnsEmptyRules()
    {
        FakeMihomoProfileTextSource textSource = new(null);
        MihomoProfileParserService service = CreateService(textSource);

        IReadOnlyList<RulePreview> rules = service.ParseActiveProfileRules();

        Assert.Empty(rules);
        Assert.Equal(1, textSource.ReadCount);
    }

    private static MihomoProfileParserService CreateService(FakeMihomoProfileTextSource textSource)
    {
        return new MihomoProfileParserService(
            textSource,
            code => new RegionMetadata(code, "region " + code, "asset-" + code),
            key => key == "ProfilePreview.CurrentConfiguration" ? "current profile" : key);
    }

    private sealed class FakeMihomoProfileTextSource(string? text) : IMihomoProfileTextSource
    {
        public int ReadCount { get; private set; }

        public string? TryReadActiveProfileText()
        {
            ReadCount++;
            return text;
        }
    }
}
