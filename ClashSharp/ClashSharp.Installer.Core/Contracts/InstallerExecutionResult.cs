namespace ClashSharp.Installer.Contracts;

/// <summary>Verified limits or follow-up work for current-user installation choices.</summary>
[Flags]
public enum InstallerPreferencesWarnings
{
    /// <summary>No follow-up is needed.</summary>
    None = 0,
    /// <summary>Windows did not enable the requested application startup task.</summary>
    StartupDenied = 1,
    /// <summary>Windows did not enable the requested proxy recovery task.</summary>
    ProxyRecoveryDenied = 2,
    /// <summary>Existing application settings were preserved instead of reseeded.</summary>
    ExistingSettingsPreserved = 4,
    /// <summary>Native taskbar confirmation remains for the foreground application.</summary>
    NativePinConfirmationPending = 8,
    /// <summary>The core installation is verified and user-only setup remains retryable.</summary>
    UserSetupPending = 16,
}

/// <summary>Describes the externally observable outcome of an installer invocation.</summary>
public enum InstallerExecutionOutcome
{
    /// <summary>The requested final state was independently verified.</summary>
    Succeeded,

    /// <summary>A deterministic safety or environment gate rejected the request.</summary>
    Blocked,

    /// <summary>The request failed after it began; a journal may require roll-forward recovery.</summary>
    Failed,

    /// <summary>
    /// A privileged participant did not prove termination or commit state; recovery must re-inspect it.
    /// </summary>
    Uncertain,

    /// <summary>The caller cancelled the request; a journal may require roll-forward recovery.</summary>
    Cancelled,
}

/// <summary>Returns a stable result without exposing raw exception messages.</summary>
/// <param name="Outcome">Coarse outcome.</param>
/// <param name="DiagnosticCode">Stable machine-readable diagnostic code.</param>
/// <param name="LastDurablePhase">Last known durable transaction phase.</param>
/// <param name="RecoveryPending">Whether a durable journal remains for a future exact-release resume.</param>
public sealed record InstallerExecutionResult(
    InstallerExecutionOutcome Outcome,
    string DiagnosticCode,
    InstallerTransactionPhase? LastDurablePhase,
    bool RecoveryPending)
{
    /// <summary>Gets verified ordinary-uninstall cleanup observations, when available.</summary>
    public InstallerDirectoryCleanupReport? DirectoryCleanupReport { get; init; }

    /// <summary>Gets observed user-setup warnings without raw platform errors.</summary>
    public InstallerPreferencesWarnings PreferencesWarnings { get; init; }
}
