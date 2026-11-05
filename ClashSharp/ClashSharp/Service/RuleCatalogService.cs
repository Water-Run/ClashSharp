using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.Model;
using ClashSharp.Model.Rules;

namespace ClashSharp.Service;

/// <summary>Parses rule preview rows from the active profile.</summary>
internal interface IRuleCatalogProfileRules
{
    /// <summary>Gets the installed configuration identity, or null for unscoped legacy adapters.</summary>
    string? StatisticsIdentity => null;
    /// <summary>Parses rules from the active imported profile.</summary>
    IReadOnlyList<RulePreview> ParseActiveProfileRules();
}

/// <summary>Stores and reads persisted rule hit counts.</summary>
internal interface IRuleCatalogHitStorage
{
    /// <summary>Ensures persisted hit-count rows exist for <paramref name="rules"/>.</summary>
    void EnsureRuleHitRows(IReadOnlyList<RulePreview> rules);

    /// <summary>Returns stored hit counts keyed by stable rule name.</summary>
    IReadOnlyDictionary<string, long> GetRuleHitCounts();

    /// <summary>Returns observations belonging only to the captured installed configuration.</summary>
    IReadOnlyDictionary<string, long> GetRuleHitCounts(string identity) => new Dictionary<string, long>();
}

/// <summary>Provides routing rule preview data for the rules page.</summary>
/// <remarks>
/// Invariants: Returned rule rows use mihomo-compatible action names.
/// Thread safety: Stateless service and safe for concurrent reads when dependencies are safe.
/// Side effects: Reads active profile rules and stored rule-hit counts through injected dependencies.
/// </remarks>
public sealed partial class RuleCatalogService
{
    private readonly IRuleCatalogProfileRules _profileRules;

    private readonly IRuleCatalogHitStorage _hitStorage;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes a new rule catalog service.</summary>
    internal RuleCatalogService(
        IRuleCatalogProfileRules profileRules,
        IRuleCatalogHitStorage hitStorage,
        Func<string, string> getString)
    {
        _profileRules = profileRules ?? throw new ArgumentNullException(nameof(profileRules));
        _hitStorage = hitStorage ?? throw new ArgumentNullException(nameof(hitStorage));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Returns rules from the active imported profile or a direct fallback row when none are available.</summary>
    /// <returns>A read-only snapshot of rule preview rows.</returns>
    public IReadOnlyList<RulePreview> GetRules()
    {
        string? identity = _profileRules.StatisticsIdentity;
        IReadOnlyList<RulePreview> parsedRules = _profileRules.ParseActiveProfileRules();
        if (identity != _profileRules.StatisticsIdentity) { return parsedRules.Select(rule => rule with { IsHitCountKnown = false }).ToArray(); }
        if (parsedRules.Count > 0)
        {
            return MergeHitCounts(parsedRules, identity);
        }

        return MergeHitCounts(
        [
            new RulePreview(GetString("RuleCatalog.BuiltInDirect.Name"), "MATCH", "*", "DIRECT", 0)
            { ProviderResourceKey = "RuleCatalog.BuiltInDirect.Name" },
        ], identity);
    }

    /// <summary>Counts rules in captured profile text using the same built-in fallback as <see cref="GetRules"/>.</summary>
    /// <remarks>
    /// This read-only summary deliberately does not initialize rule-hit rows or query hit counters;
    /// the master page consumes count value semantics and must not mutate statistics as a side effect.
    /// </remarks>
    internal static int CountRules(string? configurationText)
    {
        if (string.IsNullOrWhiteSpace(configurationText))
        {
            return 1;
        }

        int parsedCount = MihomoProfilePreviewParser.ParseRules(
            configurationText,
            static key => key).Count;
        return Math.Max(parsedCount, 1);
    }

    /// <summary>Registers rule rows in SQLite and merges stored hit counts into the visible rows.</summary>
    /// <param name="rules">Rule rows to enrich. Must not be null.</param>
    /// <param name="identity">Captured installed configuration, or null for legacy adapters.</param>
    /// <returns>Rule rows with stored hit counts applied.</returns>
    private IReadOnlyList<RulePreview> MergeHitCounts(IReadOnlyList<RulePreview> rules, string? identity)
    {
        _hitStorage.EnsureRuleHitRows(rules);
        IReadOnlyDictionary<string, long> hitCounts = identity is null
            ? _hitStorage.GetRuleHitCounts() : _hitStorage.GetRuleHitCounts(identity);
        List<RulePreview> mergedRules = new(rules.Count);
        HashSet<string> ambiguous = rules.GroupBy(BuildRuleName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);

        foreach (RulePreview rule in rules)
        {
            string ruleName = BuildRuleName(rule);
            long hitCount = hitCounts.TryGetValue(ruleName, out long count) ? count : 0;
            bool known = rule.IsHitCountKnown && identity != "unknown" && !ambiguous.Contains(ruleName);
            mergedRules.Add(rule with { HitCount = known ? hitCount : 0, IsHitCountKnown = known });
        }

        return mergedRules;
    }

    /// <summary>Builds the stable rule name used by SQLite rule-hit counters.</summary>
    /// <param name="rule">Rule row to convert.</param>
    /// <returns>Stable rule name.</returns>
    private static string BuildRuleName(RulePreview rule)
    {
        return RuleObservationKey.Create(rule.RuleType, rule.Payload);
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}
