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
/// Side effects: Reads the mainland China display policy from <see cref="AppSettingsService"/>.
/// </remarks>
public sealed class RegionDisplayService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="RegionDisplayService"/> instance.</value>
    public static RegionDisplayService Instance { get; } = new();

    /// <summary>Immutable default region display map keyed by uppercase region code.</summary>
    private static readonly FrozenDictionary<string, string> DefaultRegionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"] = "中国大陆",
        ["HK"] = "香港",
        ["MO"] = "澳门",
        ["TW"] = "台湾",
        ["JP"] = "日本",
        ["KR"] = "韩国",
        ["SG"] = "新加坡",
        ["US"] = "美国",
        ["GB"] = "英国",
        ["DE"] = "德国",
        ["FR"] = "法国",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Immutable mainland China display override map keyed by uppercase region code.</summary>
    private static readonly FrozenDictionary<string, (string Name, string FlagAssetKey)> MainlandChinaOverrides = new Dictionary<string, (string Name, string FlagAssetKey)>(StringComparer.OrdinalIgnoreCase)
    {
        ["HK"] = ("中国香港", "HK"),
        ["MO"] = ("中国澳门", "MO"),
        ["TW"] = ("中国台湾", "CN"),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new region display service instance.</summary>
    private RegionDisplayService()
    {
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

        string displayName = DefaultRegionNames.TryGetValue(normalizedCode, out string? knownName)
            ? knownName
            : normalizedCode;
        string flagAssetKey = normalizedCode;
        MainlandChinaFeatureMode featureMode = AppSettingsService.Instance.MainlandChinaFeatureMode;

        if (featureMode >= MainlandChinaFeatureMode.FlagReplacementOnly
            && MainlandChinaOverrides.TryGetValue(normalizedCode, out (string Name, string FlagAssetKey) mainlandOverride))
        {
            flagAssetKey = mainlandOverride.FlagAssetKey;
            if (featureMode >= MainlandChinaFeatureMode.FlagReplacementAndTextCompletion)
            {
                displayName = mainlandOverride.Name;
            }
        }

        return new RegionMetadata(normalizedCode, displayName, flagAssetKey);
    }
}
