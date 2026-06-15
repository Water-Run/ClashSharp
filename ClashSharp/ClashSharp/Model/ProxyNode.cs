/*
 * Proxy Node Model
 * Represents one proxy node prepared for the proxy node list
 *
 * @author: WaterRun
 * @file: Model/ProxyNode.cs
 * @date: 2026-06-15
 */

using ClashSharp.Service;

namespace ClashSharp.Model;

/// <summary>Represents one proxy node prepared for the proxy node list.</summary>
/// <param name="Name">Node display name; never null.</param>
/// <param name="Protocol">Proxy protocol display text; never null.</param>
/// <param name="Region">Resolved region display metadata.</param>
/// <param name="LatencyMilliseconds">Measured latency in milliseconds; null when not tested.</param>
/// <param name="ServerHost">Proxy server host used for latency probing; empty when unavailable.</param>
/// <param name="ServerPort">Proxy server port used for latency probing; null when unavailable.</param>
/// <remarks>
/// Invariants: String values are never null.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct ProxyNode(
    string Name,
    string Protocol,
    RegionMetadata Region,
    int? LatencyMilliseconds,
    string ServerHost = "",
    int? ServerPort = null)
{
    /// <summary>Gets the UI-filtered node display name.</summary>
    /// <value>Display name after mainland China UI replacement; never null.</value>
    public string NameDisplay => MainlandChinaTextDisplayService.Instance.Apply(Name);
}
