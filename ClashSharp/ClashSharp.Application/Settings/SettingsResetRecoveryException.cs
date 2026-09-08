namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Reports both the original reset failure and the failure to reach a recoverable runtime state.</summary>
public sealed class SettingsResetRecoveryException : AggregateException
{
    /// <summary>Initializes a recovery failure without performing presentation or restart actions.</summary>
    /// <param name="activationFailure">The reset or participant failure that initiated compensation.</param>
    /// <param name="recoveryFailure">The failure to converge, restore, or finalize rollback.</param>
    public SettingsResetRecoveryException(Exception activationFailure, Exception recoveryFailure)
        : base(
            "Settings reset could not converge or compensate every external participant.",
            activationFailure ?? throw new ArgumentNullException(nameof(activationFailure)),
            recoveryFailure ?? throw new ArgumentNullException(nameof(recoveryFailure)))
    {
        ActivationFailure = activationFailure;
        RecoveryFailure = recoveryFailure;
    }

    /// <summary>Gets the original failure that initiated recovery.</summary>
    public Exception ActivationFailure { get; }

    /// <summary>Gets the failure that prevents claiming a verified runtime state.</summary>
    public Exception RecoveryFailure { get; }
}
