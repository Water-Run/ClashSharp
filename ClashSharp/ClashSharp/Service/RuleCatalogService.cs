/*
 * Rule Catalog Service
 * Provides routing rule data from the active profile with a built-in direct fallback row
 *
 * @author: WaterRun
 * @file: Service/RuleCatalogService.cs
 * @date: 2026-06-15
 */

using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides routing rule preview data for the rules page.</summary>
/// <remarks>
/// Invariants: Returned rule rows use mihomo-compatible action names.
/// Thread safety: Stateless service and safe for concurrent reads.
/// Side effects: Reads the active profile file when one is selected.
/// </remarks>
public sealed class RuleCatalogService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="RuleCatalogService"/> instance.</value>
    public static RuleCatalogService Instance { get; } = new();

    /// <summary>Initializes the rule catalog service.</summary>
    private RuleCatalogService()
    {
    }

    /// <summary>Returns rules from the active imported profile or a direct fallback row when none are available.</summary>
    /// <returns>A read-only snapshot of rule preview rows.</returns>
    public IReadOnlyList<RulePreview> GetRules()
    {
        IReadOnlyList<RulePreview> parsedRules = MihomoProfileParserService.Instance.ParseActiveProfileRules();
        if (parsedRules.Count > 0)
        {
            return MergeHitCounts(parsedRules);
        }

        return MergeHitCounts(
        [
            new RulePreview("内置直连", "MATCH", "*", "DIRECT", 0),
        ]);
    }

    /// <summary>Registers rule rows in SQLite and merges stored hit counts into the visible rows.</summary>
    /// <param name="rules">Rule rows to enrich. Must not be null.</param>
    /// <returns>Rule rows with stored hit counts applied.</returns>
    private static IReadOnlyList<RulePreview> MergeHitCounts(IReadOnlyList<RulePreview> rules)
    {
        LogStorageService storage = LogStorageService.Instance;
        storage.EnsureRuleHitRows(rules);
        IReadOnlyDictionary<string, long> hitCounts = storage.GetRuleHitCounts();
        List<RulePreview> mergedRules = new(rules.Count);

        foreach (RulePreview rule in rules)
        {
            string ruleName = BuildRuleName(rule);
            long hitCount = hitCounts.TryGetValue(ruleName, out long count) ? count : 0;
            mergedRules.Add(rule with { HitCount = hitCount });
        }

        return mergedRules;
    }

    /// <summary>Builds the stable rule name used by SQLite rule-hit counters.</summary>
    /// <param name="rule">Rule row to convert.</param>
    /// <returns>Stable rule name.</returns>
    private static string BuildRuleName(RulePreview rule)
    {
        return $"{rule.RuleType},{rule.Payload},{rule.Action}";
    }
}
