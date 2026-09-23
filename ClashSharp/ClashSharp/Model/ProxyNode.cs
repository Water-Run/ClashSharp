namespace ClashSharp.Model;

/// <summary>Represents one proxy node prepared for the proxy node list.</summary>
/// <param name="Name">Node display name; never null.</param>
/// <param name="Protocol">Proxy protocol display text; never null.</param>
/// <param name="Region">Resolved region display metadata.</param>
/// <param name="LatencyMilliseconds">Measured latency in milliseconds; null when not tested.</param>
/// <param name="ServerHost">Proxy server host used for latency probing; empty when unavailable.</param>
/// <param name="ServerPort">Proxy server port used for latency probing; null when unavailable.</param>
/// <param name="WasLatencyTested">Whether this endpoint has a completed measurement in this application session.</param>
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
    int? ServerPort = null,
    bool WasLatencyTested = false);
