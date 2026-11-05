using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Service;

internal interface IMasterInfoTileLayoutSettings
{
    string MasterInfoTileLayout { get; }

    string MasterInfoTilePresets { get; }

    Task SaveInfoTileLayoutAsync(string layout, CancellationToken cancellationToken);

    Task SaveInfoTilePresetsAsync(string presets, CancellationToken cancellationToken);
}

internal sealed class MasterInfoTileLayoutService : IMasterInfoTileLayoutService
{
    private const string PreviousDefaultPresets = "@daily;@traffic;@subscriptions;@diagnostics;@minimal";

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

    public async Task<IReadOnlyList<string>> SaveLayoutAsync(
        IEnumerable<string> tileIds,
        IReadOnlyCollection<string> availableTileIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tileIds);
        ArgumentNullException.ThrowIfNull(availableTileIds);

        IReadOnlyList<string> normalized = Normalize(tileIds, availableTileIds);
        cancellationToken.ThrowIfCancellationRequested();
        await _settings.SaveInfoTileLayoutAsync(string.Join(",", normalized), cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public IReadOnlyList<string> GetRecommendedLayout(IReadOnlyCollection<string> availableTileIds) =>
        Normalize(DefaultLayout, availableTileIds);

    /// <summary>Reads the saved list, displaying the current defaults for the exact previous default list or an unreadable value.</summary>
    public IReadOnlyList<MasterInfoTilePresetEntry> GetPresets()
    {
        string saved = _settings.MasterInfoTilePresets;
        return saved != PreviousDefaultPresets
            && MasterInfoTilePresetList.TryParse(saved, out IReadOnlyList<MasterInfoTilePresetEntry> entries, out _)
            ? entries
            : DefaultPresets();
    }

    /// <summary>Persists the template list in the given order and returns the stored entries.</summary>
    public async Task<IReadOnlyList<MasterInfoTilePresetEntry>> SavePresetsAsync(
        IEnumerable<MasterInfoTilePresetEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string canonical = MasterInfoTilePresetList.Format(entries);
        if (!MasterInfoTilePresetList.TryParse(canonical, out IReadOnlyList<MasterInfoTilePresetEntry> normalized, out _))
        {
            throw new ArgumentException("The template list exceeds its limits.", nameof(entries));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _settings.SaveInfoTilePresetsAsync(canonical, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    /// <summary>Restores and persists the built-in template list, removing saved custom templates.</summary>
    public Task<IReadOnlyList<MasterInfoTilePresetEntry>> RestoreDefaultPresetsAsync(CancellationToken cancellationToken) =>
        SavePresetsAsync(DefaultPresets(), cancellationToken);

    private static IReadOnlyList<MasterInfoTilePresetEntry> DefaultPresets() =>
        MasterInfoTilePresetList.BuiltInIds.Select(MasterInfoTilePresetEntry.BuiltIn).ToArray();

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
