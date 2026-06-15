/*
 * Proxy Recovery Mode Model
 * Defines startup recovery behavior for stale Windows proxy state after abnormal exits
 *
 * @author: WaterRun
 * @file: Model/ProxyRecoveryMode.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Enumerates startup recovery actions for stale Windows system proxy state.</summary>
/// <remarks>
/// Invariants: Each value maps to one explicit user-selected recovery action.
/// Thread safety: Enum values are immutable and inherently thread-safe.
/// Side effects: None.
/// </remarks>
public enum ProxyRecoveryMode
{
    /// <summary>Leave the detected Windows proxy state unchanged during startup recovery.</summary>
    DoNothing = 0,

    /// <summary>Restore Windows proxy settings to the Clash#-enabled state during startup recovery.</summary>
    EnableProxy = 1,

    /// <summary>Disable Windows proxy settings during startup recovery.</summary>
    DisableProxy = 2,
}
