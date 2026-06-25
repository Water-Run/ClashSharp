/*
 * Rule Catalog Service Tests
 * Verifies rule catalog rows are composed through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/RuleCatalogServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for rule catalog composition.</summary>
public sealed class RuleCatalogServiceTests
{
    /// <summary>Verifies an empty active profile produces a localized direct fallback row with stored hit counts.</summary>
    [Fact]
    public void GetRules_WhenActiveProfileHasNoRules_ReturnsLocalizedFallbackWithHitCounts()
    {
        FakeRuleCatalogProfileRules profileRules = new();
        FakeRuleCatalogHitStorage hitStorage = new()
        {
            HitCounts =
            {
                ["MATCH,*,DIRECT"] = 7,
            },
        };
        RuleCatalogService service = CreateService(profileRules, hitStorage);

        IReadOnlyList<RulePreview> rules = service.GetRules();

        RulePreview rule = Assert.Single(rules);
        Assert.Equal("localized direct", rule.ProviderName);
        Assert.Equal("MATCH", rule.RuleType);
        Assert.Equal("*", rule.Payload);
        Assert.Equal("DIRECT", rule.Action);
        Assert.Equal(7, rule.HitCount);
        RulePreview ensuredRule = Assert.Single(hitStorage.EnsuredRules);
        Assert.Equal("localized direct", ensuredRule.ProviderName);
    }

    /// <summary>Verifies parsed active profile rules are enriched with stored hit counts without using fallback rows.</summary>
    [Fact]
    public void GetRules_WhenActiveProfileHasRules_MergesHitCountsIntoParsedRules()
    {
        FakeRuleCatalogProfileRules profileRules = new()
        {
            Rules =
            [
                new RulePreview("Provider", "DOMAIN-SUFFIX", "example.com", "PROXY", 0),
            ],
        };
        FakeRuleCatalogHitStorage hitStorage = new()
        {
            HitCounts =
            {
                ["DOMAIN-SUFFIX,example.com,PROXY"] = 11,
            },
        };
        RuleCatalogService service = CreateService(profileRules, hitStorage);

        IReadOnlyList<RulePreview> rules = service.GetRules();

        RulePreview rule = Assert.Single(rules);
        Assert.Equal("Provider", rule.ProviderName);
        Assert.Equal("DOMAIN-SUFFIX", rule.RuleType);
        Assert.Equal("example.com", rule.Payload);
        Assert.Equal("PROXY", rule.Action);
        Assert.Equal(11, rule.HitCount);
    }

    private static RuleCatalogService CreateService(
        FakeRuleCatalogProfileRules profileRules,
        FakeRuleCatalogHitStorage hitStorage)
    {
        return new RuleCatalogService(
            profileRules,
            hitStorage,
            key => key == "RuleCatalog.BuiltInDirect.Name" ? "localized direct" : key);
    }

    private sealed class FakeRuleCatalogProfileRules : IRuleCatalogProfileRules
    {
        public IReadOnlyList<RulePreview> Rules { get; init; } = [];

        public IReadOnlyList<RulePreview> ParseActiveProfileRules()
        {
            return Rules;
        }
    }

    private sealed class FakeRuleCatalogHitStorage : IRuleCatalogHitStorage
    {
        public Dictionary<string, long> HitCounts { get; } = [];

        public List<RulePreview> EnsuredRules { get; } = [];

        public void EnsureRuleHitRows(IReadOnlyList<RulePreview> rules)
        {
            EnsuredRules.AddRange(rules);
        }

        public IReadOnlyDictionary<string, long> GetRuleHitCounts()
        {
            return HitCounts;
        }
    }
}
