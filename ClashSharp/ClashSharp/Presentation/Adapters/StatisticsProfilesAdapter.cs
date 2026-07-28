using System;
using System.Collections.Generic;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="ProfileCatalogService"/> to canonical profile-name lookup.</summary>
/// <remarks>
/// Invariants: Returns unmodified profile names keyed by profile identifiers.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads profile catalog metadata.
/// </remarks>
internal sealed class StatisticsProfilesAdapter : IStatisticsProfiles
{
    /// <summary>Wrapped profile catalog service.</summary>
    private readonly ProfileCatalogService _profiles;

    /// <summary>Initializes a statistics profiles adapter.</summary>
    /// <param name="profiles">Profile catalog service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> is null.</exception>
    public StatisticsProfilesAdapter(ProfileCatalogService profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    /// <summary>Gets canonical profile names keyed by profile identifier.</summary>
    /// <returns>Unmodified profile names keyed by identifier.</returns>
    public IReadOnlyDictionary<string, string> GetProfileDisplayNamesById()
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (ConfigurationProfile profile in _profiles.GetProfiles())
        {
            names[profile.Id] = profile.Name;
        }

        return names;
    }
}
