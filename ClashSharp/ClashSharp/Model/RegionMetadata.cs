/*
 * Region Metadata Model
 * Represents display metadata for proxy node regions and flag assets
 *
 * @author: WaterRun
 * @file: Model/RegionMetadata.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Represents display metadata for a proxy node region.</summary>
/// <param name="RegionCode">ISO-style region code; never null.</param>
/// <param name="DisplayName">Localized display name; never null.</param>
/// <param name="FlagAssetKey">Flag asset key used by the UI; never null.</param>
/// <remarks>
/// Invariants: String values are never null and region codes are uppercase.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct RegionMetadata(string RegionCode, string DisplayName, string FlagAssetKey)
{
    /// <summary>Gets the packaged flag image path used by WinUI bindings.</summary>
    /// <value>MSIX asset URI for the resolved flag key; never null.</value>
    public string FlagAssetPath => $"ms-appx:///Assets/Flags/{FlagAssetKey.ToLowerInvariant()}.png";
}
