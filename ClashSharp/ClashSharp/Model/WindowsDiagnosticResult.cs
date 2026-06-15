/*
 * Windows Diagnostic Result Model
 * Represents the outcome of one Windows-native network diagnostic
 *
 * @author: WaterRun
 * @file: Model/WindowsDiagnosticResult.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Represents the outcome of one Windows-native network diagnostic.</summary>
/// <param name="Target">Diagnostic target.</param>
/// <param name="DisplayName">Target display name; never null.</param>
/// <param name="IsHealthy">True when the target appears ready for Clash# proxy usage.</param>
/// <param name="Message">Short user-facing diagnostic message; never null.</param>
/// <param name="Detail">Optional diagnostic detail; never null.</param>
/// <remarks>
/// Invariants: String values are never null.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct WindowsDiagnosticResult(
    WindowsDiagnosticTarget Target,
    string DisplayName,
    bool IsHealthy,
    string Message,
    string Detail);
