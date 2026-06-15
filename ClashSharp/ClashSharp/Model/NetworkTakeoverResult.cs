/*
 * Network Takeover Result Model
 * Represents the outcome of applying a Clash# master takeover mode
 *
 * @author: WaterRun
 * @file: Model/NetworkTakeoverResult.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Represents the outcome of applying a Clash# master takeover mode.</summary>
/// <param name="Mode">The takeover mode that was applied.</param>
/// <param name="CoreRunning">True when the mihomo core is expected to be running after application.</param>
/// <param name="SystemProxyEnabled">True when Windows system proxy is expected to be enabled after application.</param>
/// <param name="Message">Human-readable outcome text; never null.</param>
/// <remarks>
/// Invariants: <paramref name="Message"/> is never null.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct NetworkTakeoverResult(
    ClashSharpMode Mode,
    bool CoreRunning,
    bool SystemProxyEnabled,
    string Message);
