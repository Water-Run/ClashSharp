using System.Collections.Generic;

namespace ClashSharp.ViewModel;

/// <summary>Profile lookup contract used by <see cref="StatisticsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Keys are profile identifiers and values are display names.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May read profile catalog metadata.
/// </remarks>
internal interface IStatisticsProfiles
{
    /// <summary>Gets profile display names keyed by profile identifier.</summary>
    /// <returns>Profile display names keyed by identifier.</returns>
    IReadOnlyDictionary<string, string> GetProfileDisplayNamesById();
}
