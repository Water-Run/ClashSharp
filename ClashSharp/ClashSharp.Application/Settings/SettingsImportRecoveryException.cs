namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Reports an import failure that cannot restore both its retained baseline and runtime.</summary>
public sealed class SettingsImportRecoveryException : AggregateException
{
    /// <summary>Initializes a recovery failure without requesting application shutdown or restart.</summary>
    /// <param name="activationFailure">The failure after the package transaction began.</param>
    /// <param name="recoveryFailure">The rollback or compensation failure.</param>
    public SettingsImportRecoveryException(Exception activationFailure, Exception recoveryFailure)
        : base(
            "Settings import could not restore a consistent durable and external generation.",
            activationFailure ?? throw new ArgumentNullException(nameof(activationFailure)),
            recoveryFailure ?? throw new ArgumentNullException(nameof(recoveryFailure)))
    {
        ActivationFailure = activationFailure;
        RecoveryFailure = recoveryFailure;
    }

    /// <summary>Gets the original activation failure.</summary>
    public Exception ActivationFailure { get; }

    /// <summary>Gets the failure that prevents declaring recovery complete.</summary>
    public Exception RecoveryFailure { get; }
}
