using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Service;

internal interface IMasterInfoTileLayoutSettings
{
    string MasterInfoTileLayout { get; set; }
}

internal sealed class MasterInfoTileLayoutService : IMasterInfoTileLayoutService
{
    private readonly IMasterInfoTileLayoutSettings _settings;

    public static IReadOnlyList<string> DefaultLayout { get; } = SettingsRegistry.Default
        .Get(SettingsRegistry.Keys.MasterInfoTileLayout.Value)
        .DefaultValue
        .CanonicalText
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public MasterInfoTileLayoutService(IMasterInfoTileLayoutSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IReadOnlyList<string> GetLayout(IReadOnlyCollection<string> availableTileIds)
    {
        ArgumentNullException.ThrowIfNull(availableTileIds);

        string persistedLayout = _settings.MasterInfoTileLayout;
        IReadOnlyList<string> normalized = Normalize(Parse(persistedLayout), availableTileIds);
        if (normalized.Count > 0 || persistedLayout.Length == 0)
        {
            return normalized;
        }

        return Normalize(DefaultLayout, availableTileIds);
    }

    public IReadOnlyList<string> SaveLayout(
        IEnumerable<string> tileIds,
        IReadOnlyCollection<string> availableTileIds)
    {
        ArgumentNullException.ThrowIfNull(tileIds);
        ArgumentNullException.ThrowIfNull(availableTileIds);

        IReadOnlyList<string> normalized = Normalize(tileIds, availableTileIds);
        _settings.MasterInfoTileLayout = string.Join(",", normalized);
        return normalized;
    }

    private static IReadOnlyList<string> Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyList<string> Normalize(
        IEnumerable<string> tileIds,
        IReadOnlyCollection<string> availableTileIds)
    {
        Dictionary<string, string> canonicalIds = availableTileIds
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(static id => id, static id => id, StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string tileId in tileIds)
        {
            if (canonicalIds.TryGetValue(tileId, out string? canonicalId)
                && seen.Add(canonicalId))
            {
                result.Add(canonicalId);
            }
        }

        return result;
    }
}
