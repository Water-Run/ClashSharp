/*
 * ClashSharp Takeover Mode Model
 * Defines the user-facing network takeover states exposed by the master control surface
 *
 * @author: WaterRun
 * @file: Model/ClashSharpMode.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Enumerates the primary network takeover modes exposed by Clash#.</summary>
/// <remarks>
/// Invariants: Each value maps to one visible master-control state.
/// Thread safety: Enum values are immutable and inherently thread-safe.
/// Side effects: None.
/// </remarks>
public enum ClashSharpMode
{
    /// <summary>Clash# is not running and does not take over Windows networking.</summary>
    Disabled = 0,

    /// <summary>The core is running but defaults traffic to direct routing.</summary>
    Standby = 1,

    /// <summary>Traffic is routed through mihomo rules, equivalent to rule mode.</summary>
    RuleTakeover = 2,

    /// <summary>All eligible traffic is routed through the selected proxy, equivalent to global mode.</summary>
    FullTakeover = 3,

    /// <summary>The desired takeover state failed and requires user-visible remediation.</summary>
    Faulted = 4,
}
