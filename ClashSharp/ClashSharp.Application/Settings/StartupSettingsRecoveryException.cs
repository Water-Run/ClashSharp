namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Reports that a startup change could not restore its preference and observed registration.</summary>
public sealed class StartupSettingsRecoveryException : AggregateException
{
    /// <summary>Preserves the application failure and all incomplete compensation diagnostics.</summary>
    /// <param name="applicationFailure">The original registration or persistence failure.</param>
    /// <param name="recoveryFailure">Failure restoring or verifying either independent baseline.</param>
    public StartupSettingsRecoveryException(Exception applicationFailure, Exception recoveryFailure)
        : base(
            "Startup settings could not restore their original preference and platform registration.",
            applicationFailure ?? throw new ArgumentNullException(nameof(applicationFailure)),
            recoveryFailure ?? throw new ArgumentNullException(nameof(recoveryFailure)))
    {
        ApplicationFailure = applicationFailure;
        RecoveryFailure = recoveryFailure;
    }

    /// <summary>Gets the original application failure.</summary>
    public Exception ApplicationFailure { get; }

    /// <summary>Gets the compensation failure without losing nested diagnostics.</summary>
    public Exception RecoveryFailure { get; }
}
