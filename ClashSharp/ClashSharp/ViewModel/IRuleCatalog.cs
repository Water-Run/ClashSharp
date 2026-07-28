using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Rule catalog contract used by <see cref="RulesViewModel"/>.</summary>
/// <remarks>
/// Invariants: Returned rule rows are safe to bind directly.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May read active profile rule metadata.
/// </remarks>
internal interface IRuleCatalog
{
    /// <summary>Gets visible rule preview rows.</summary>
    /// <returns>Read-only rule preview rows.</returns>
    IReadOnlyList<RulePreview> GetRules();
}
