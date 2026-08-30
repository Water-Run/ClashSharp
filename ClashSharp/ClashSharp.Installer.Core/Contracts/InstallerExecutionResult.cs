namespace ClashSharp.Installer.Contracts;

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
    bool RecoveryPending);
