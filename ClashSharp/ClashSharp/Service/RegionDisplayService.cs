/*
 * Region Display Service
 * Resolves proxy node region display names and flag asset keys with mainland China display policy support
 *
 * @author: WaterRun
 * @file: Service/RegionDisplayService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Resolves proxy node region display names and flag asset keys.</summary>
/// <remarks>
/// Invariants: Known region mappings are immutable after type initialization.
/// Thread safety: Public methods are thread-safe and read immutable mapping data.
/// Side effects: Delegated to injected settings and localization providers.
/// </remarks>
public sealed class RegionDisplayService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="RegionDisplayService"/> instance.</value>
    public static RegionDisplayService Instance { get; } = RegionDisplayServiceFactory.CreateDefault();

    private readonly Func<MainlandChinaFeatureMode> _getFeatureMode;

    private readonly Func<string, string> _getString;

    /// <summary>Immutable default region resource-key map keyed by uppercase region code.</summary>
    private static readonly FrozenDictionary<string, string> DefaultRegionKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"] = "Region.CN",
        ["HK"] = "Region.HK",
        ["MO"] = "Region.MO",
        ["TW"] = "Region.TW",
        ["JP"] = "Region.JP",
        ["KR"] = "Region.KR",
        ["SG"] = "Region.SG",
        ["US"] = "Region.US",
        ["GB"] = "Region.GB",
        ["DE"] = "Region.DE",
        ["FR"] = "Region.FR",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Immutable mainland China display override map keyed by uppercase region code.</summary>
    private static readonly FrozenDictionary<string, (string NameKey, string FlagAssetKey)> MainlandChinaOverrides = new Dictionary<string, (string NameKey, string FlagAssetKey)>(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"] = ("Region.MainlandChina.CN", "CN"),
        ["HK"] = ("Region.MainlandChina.HK", "HK"),
        ["MO"] = ("Region.MainlandChina.MO", "MO"),
        ["TW"] = ("Region.MainlandChina.TW", "CN"),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new region display service instance.</summary>
    /// <param name="getFeatureMode">Provider for the active mainland China display policy. Must not be null.</param>
    /// <param name="getString">Localized string resolver. Must not be null.</param>
    internal RegionDisplayService(Func<MainlandChinaFeatureMode> getFeatureMode, Func<string, string> getString)
    {
        _getFeatureMode = getFeatureMode ?? throw new ArgumentNullException(nameof(getFeatureMode));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Resolves display metadata for <paramref name="regionCode"/>.</summary>
    /// <param name="regionCode">Region code to resolve. Must not be null.</param>
    /// <returns>Resolved <see cref="RegionMetadata"/> with display name and flag asset key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="regionCode"/> is null.</exception>
    public RegionMetadata Resolve(string regionCode)
    {
        ArgumentNullException.ThrowIfNull(regionCode);

        string normalizedCode = regionCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            normalizedCode = "UN";
        }

        string displayName = DefaultRegionKeys.TryGetValue(normalizedCode, out string? knownNameKey)
            ? _getString(knownNameKey)
            : normalizedCode;
        string flagAssetKey = normalizedCode;
        MainlandChinaFeatureMode featureMode = _getFeatureMode();

        if (featureMode >= MainlandChinaFeatureMode.FlagReplacementOnly
            && MainlandChinaOverrides.TryGetValue(normalizedCode, out (string NameKey, string FlagAssetKey) mainlandOverride))
        {
            flagAssetKey = mainlandOverride.FlagAssetKey;
            if (featureMode >= MainlandChinaFeatureMode.FlagReplacementAndTextCompletion)
            {
                displayName = _getString(mainlandOverride.NameKey);
            }
        }

        return new RegionMetadata(normalizedCode, displayName, flagAssetKey);
    }
}
