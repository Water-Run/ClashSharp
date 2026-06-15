/*
 * Proxy Recovery Result Model
 * Represents the outcome of a startup Windows proxy recovery attempt
 *
 * @author: WaterRun
 * @file: Model/ProxyRecoveryResult.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Represents the outcome of a startup Windows proxy recovery attempt.</summary>
/// <param name="WasApplied">True when a recovery action changed Windows proxy state.</param>
/// <param name="Message">Human-readable recovery outcome text; never null.</param>
/// <remarks>
/// Invariants: <paramref name="Message"/> is never null.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct ProxyRecoveryResult(bool WasApplied, string Message);
