namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Preserves both an unsuccessful sampling change and incomplete recovery.</summary>
public sealed class ConnectionSamplingSettingsRecoveryException : AggregateException
{
    /// <summary>Creates the diagnostic for a change whose baseline could not be completely restored.</summary>
    /// <param name="applicationFailure">Original application failure.</param>
    /// <param name="recoveryFailure">Failure while restoring the verified preference and runtime baseline.</param>
    public ConnectionSamplingSettingsRecoveryException(Exception applicationFailure, Exception recoveryFailure)
        : base("Connection sampling failed and its previous state could not be restored.",
            applicationFailure, recoveryFailure)
    {
        ApplicationFailure = applicationFailure;
        RecoveryFailure = recoveryFailure;
    }

    /// <summary>Gets the original application failure.</summary>
    public Exception ApplicationFailure { get; }

    /// <summary>Gets the failure that prevented complete recovery.</summary>
    public Exception RecoveryFailure { get; }
}
