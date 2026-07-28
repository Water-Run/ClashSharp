using System;

namespace ClashSharp.Model;

/// <summary>Runtime status displayed in the tray status submenu.</summary>
/// <param name="CurrentNodeName">Current proxy node name; empty when unavailable.</param>
/// <param name="LatencyMilliseconds">Measured latency in milliseconds; null when unavailable.</param>
public readonly record struct TrayStatusSnapshot(string CurrentNodeName, int? LatencyMilliseconds)
{
    /// <summary>Unavailable status snapshot.</summary>
    public static TrayStatusSnapshot Unavailable { get; } = new(string.Empty, null);

    /// <summary>Gets whether the snapshot contains a current node name.</summary>
    public bool HasCurrentNode => !string.IsNullOrWhiteSpace(CurrentNodeName);
}
