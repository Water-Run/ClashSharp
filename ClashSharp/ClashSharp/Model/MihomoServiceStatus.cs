using System;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Model;

/// <summary>Describes whether a mihomo service status came from a conclusive SCM observation.</summary>
public enum MihomoServiceObservationState
{
    /// <summary>No conclusive service state has been observed.</summary>
    Unknown = 0,

    /// <summary>The service state was conclusively observed through SCM.</summary>
    Confirmed = 1,
}

/// <summary>Represents the Windows service deployment state used by transparent proxy settings.</summary>
/// <param name="IsInstalled">True when the service exists.</param>
/// <param name="IsRunning">True only when authenticated IPC reports a running service-owned child whose controller is ready.</param>
/// <param name="Message">User-facing status message; consumers normalize the default struct value.</param>
public readonly record struct MihomoServiceStatus(bool IsInstalled, bool IsRunning, string Message)
{
    /// <summary>
    /// Gets whether SCM conclusively reported the service host as running. This is
    /// deliberately separate from <see cref="IsRunning"/>, which is true only
    /// after authenticated IPC confirms a controller-ready running child.
    /// </summary>
    public bool IsScmRunning { get; init; } = IsRunning;

    /// <summary>Gets whether authenticated IPC reports a controller-ready service-owned mihomo child.</summary>
    /// <remarks>Retained as a compatibility alias for <see cref="IsRunning"/>.</remarks>
    public bool HasRunningChild => IsRunning;

    /// <summary>
    /// Gets whether the authenticated service-owned runtime is running and its
    /// controller has completed the service readiness probe.
    /// </summary>
    public bool IsReady => IsRunning;

    /// <summary>Gets the negotiated IPC protocol version, when a handshake succeeded.</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>Gets the service-host process session observed through IPC.</summary>
    public Guid? ServiceSessionId { get; init; }

    /// <summary>Gets the service build version observed through IPC.</summary>
    public string? ServiceVersion { get; init; }

    /// <summary>Gets the service-owned child lifecycle state observed through IPC.</summary>
    public MihomoServiceChildState? ChildState { get; init; }

    /// <summary>Gets the service-owned child PID while ownership exists.</summary>
    public int? ChildProcessId { get; init; }

    /// <summary>Gets the exact active runtime generation reported by the service child.</summary>
    public long? ActiveGeneration { get; init; }

    /// <summary>Gets the exact active configuration hash reported by the service child.</summary>
    public string? ActiveConfigurationHash { get; init; }

    /// <summary>Gets a stable IPC failure code when SCM is observable but readiness is not.</summary>
    public string? IpcFailureCode { get; init; }

    /// <summary>Gets why the Installer-owned owner/token association is unavailable.</summary>
    public string? ProvisioningFailureCode { get; init; }

    /// <summary>Gets a stable failure code when compensating SCM shutdown was not confirmed.</summary>
    public string? CleanupFailureCode { get; init; }

    /// <summary>Gets whether the status is based on a conclusive SCM observation.</summary>
    public MihomoServiceObservationState ObservationState { get; init; } =
        MihomoServiceObservationState.Confirmed;

    /// <summary>Gets whether the installation state is known conclusively.</summary>
    public bool IsKnown => ObservationState is MihomoServiceObservationState.Confirmed;

    /// <summary>
    /// Gets whether the service-owned child has conclusively released runtime ownership.
    /// An idle Installer-managed host may remain running; in that case only a coherent,
    /// authenticated stopped snapshot is sufficient proof.
    /// </summary>
    public bool HasReleasedChildOwnership =>
        IsKnown
        && (!IsScmRunning
            || (!IsRunning
                && ProtocolVersion == MihomoServiceIpcProtocol.CurrentVersion
                && ServiceSessionId is Guid sessionId
                && sessionId != Guid.Empty
                && ChildState == MihomoServiceChildState.Stopped
                && ChildProcessId is null
                && ActiveGeneration is null
                && ActiveConfigurationHash is null));

    /// <summary>Creates a status for an unobserved or inconclusive service state.</summary>
    /// <param name="message">User-facing status message; must not be null.</param>
    public static MihomoServiceStatus Unknown(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new MihomoServiceStatus(false, false, message)
        {
            ObservationState = MihomoServiceObservationState.Unknown,
        };
    }
}
