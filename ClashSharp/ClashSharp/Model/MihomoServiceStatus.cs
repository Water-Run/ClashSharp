using System;

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
/// <param name="IsRunning">True when the service exists and reports running.</param>
/// <param name="Message">User-facing status message; consumers normalize the default struct value.</param>
public readonly record struct MihomoServiceStatus(bool IsInstalled, bool IsRunning, string Message)
{
    /// <summary>Gets whether the status is based on a conclusive SCM observation.</summary>
    public MihomoServiceObservationState ObservationState { get; init; } =
        MihomoServiceObservationState.Confirmed;

    /// <summary>Gets whether the installation state is known conclusively.</summary>
    public bool IsKnown => ObservationState is MihomoServiceObservationState.Confirmed;

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
