/*
 * Rule Catalog Service Factory
 * Wires production dependencies for routing rule catalog data
 *
 * @author: WaterRun
 * @file: Service/RuleCatalogServiceFactory.cs
 * @date: 2026-06-25
 */

using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class RuleCatalogService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="RuleCatalogService"/> instance.</value>
    public static RuleCatalogService Instance { get; } = RuleCatalogServiceFactory.CreateDefault();
}

/// <summary>Creates rule catalog service instances with production dependencies.</summary>
internal static class RuleCatalogServiceFactory
{
    /// <summary>Creates the default rule catalog service.</summary>
    /// <returns>A service wired to active profile parsing, log storage, and localization resources.</returns>
    public static RuleCatalogService CreateDefault()
    {
        return new RuleCatalogService(
            new RuleCatalogProfileRulesAdapter(MihomoProfileParserService.Instance),
            new RuleCatalogHitStorageAdapter(LogStorageService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class RuleCatalogProfileRulesAdapter(MihomoProfileParserService parser) : IRuleCatalogProfileRules
{
    public IReadOnlyList<RulePreview> ParseActiveProfileRules()
    {
        return parser.ParseActiveProfileRules();
    }
}

internal sealed class RuleCatalogHitStorageAdapter(LogStorageService storage) : IRuleCatalogHitStorage
{
    public void EnsureRuleHitRows(IReadOnlyList<RulePreview> rules)
    {
        storage.EnsureRuleHitRows(rules);
    }

    public IReadOnlyDictionary<string, long> GetRuleHitCounts()
    {
        return storage.GetRuleHitCounts();
    }
}
