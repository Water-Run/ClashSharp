/*
 * Mihomo Profile Parser Service Tests
 * Verifies active-profile parsing through injected text sources
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/MihomoProfileParserServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for the mihomo profile parser service.</summary>
public sealed class MihomoProfileParserServiceTests
{
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
