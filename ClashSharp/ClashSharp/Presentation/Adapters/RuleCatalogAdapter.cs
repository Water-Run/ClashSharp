using System;
using System.Collections.Generic;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="RuleCatalogService"/> to rule catalog reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null rule catalog service.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads active profile rule metadata.
/// </remarks>
internal sealed class RuleCatalogAdapter : IRuleCatalog
{
    /// <summary>Wrapped rule catalog service.</summary>
    private readonly RuleCatalogService _rules;

    /// <summary>Initializes a rule catalog adapter.</summary>
    /// <param name="rules">Rule catalog service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is null.</exception>
    public RuleCatalogAdapter(RuleCatalogService rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>Gets visible rule preview rows.</summary>
    /// <returns>Read-only rule preview rows.</returns>
    public IReadOnlyList<RulePreview> GetRules()
    {
        return _rules.GetRules();
    }
}
