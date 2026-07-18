using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Network;

/// <summary>Represents one probed external network state.</summary>
/// <param name="Mode">Observed takeover mode.</param>
/// <param name="CoreRunning">Whether the core is observed running.</param>
/// <param name="SystemProxyEnabled">Whether Windows system proxy is observed enabled.</param>
/// <param name="TransparentProxyEnabled">Whether TUN transparent proxy is observed active.</param>
/// <param name="MixedPort">Observed effective mixed port.</param>
/// <param name="StateHash">Stable hash or identity of the complete observed state.</param>
/// <param name="IsKnown">Whether the adapter can classify the complete state safely.</param>
public sealed record NetworkStateSnapshot(
    ClashSharpMode Mode,
    bool CoreRunning,
    bool SystemProxyEnabled,
    bool TransparentProxyEnabled,
    int MixedPort,
    string StateHash,
    bool IsKnown = true);

/// <summary>Captures the immutable baseline, desired state, and compensation identity for one transition.</summary>
/// <param name="Intent">Original desired network intent.</param>
/// <param name="Baseline">Probed verified baseline network state.</param>
/// <param name="Desired">Fully planned desired network state.</param>
/// <param name="BaselineHash">Aggregate baseline hash including durable desired/applied state.</param>
/// <param name="DesiredHash">Aggregate desired hash including durable desired/applied state.</param>
/// <param name="CompensationData">Versioned opaque data sufficient to restore the baseline.</param>
public sealed record NetworkPlan(
    NetworkIntent Intent,
    NetworkStateSnapshot Baseline,
    NetworkStateSnapshot Desired,
    string BaselineHash,
    string DesiredHash,
    string CompensationData);

/// <summary>Returns the verified effective state of a successful network transition.</summary>
/// <param name="Mode">Verified effective takeover mode.</param>
/// <param name="CoreRunning">Whether the core is verified running.</param>
/// <param name="SystemProxyEnabled">Whether Windows system proxy is verified enabled.</param>
/// <param name="TransparentProxyEnabled">Whether TUN is verified active.</param>
/// <param name="MixedPort">Verified effective mixed port.</param>
/// <param name="StateHash">Verified aggregate external-state identity.</param>
public sealed record NetworkTransitionResult(
    ClashSharpMode Mode,
    bool CoreRunning,
    bool SystemProxyEnabled,
    bool TransparentProxyEnabled,
    int MixedPort,
    string StateHash);
