/*
 * Profile Catalog Identifiers
 * Defines stable profile identifiers shared across catalog, settings, parser, and statistics services
 *
 * @author: WaterRun
 * @file: Service/ProfileCatalogIds.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Service;

/// <summary>Defines stable profile identifiers used across Clash# services.</summary>
/// <remarks>
/// Invariants: Identifier values are persisted and must remain backward-compatible.
/// Thread safety: Constants are immutable and inherently thread-safe.
/// Side effects: None.
/// </remarks>
public static class ProfileCatalogIds
{
    /// <summary>Built-in direct profile used before the user imports a subscription or local profile.</summary>
    public const string BuiltInDirect = "builtin-direct";
}
