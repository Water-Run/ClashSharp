namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Describes the durable outcome of a top-level mutation.</summary>
public enum MutationOutcome
{
    /// <summary>The desired target was applied and verified.</summary>
    Succeeded,

    /// <summary>The request was cancelled before any external side effect.</summary>
    Cancelled,

    /// <summary>The baseline was restored and verified after a failed or cancelled change.</summary>
    Compensated,

    /// <summary>An uncommitted recovery obligation remains and ordinary admission is blocked.</summary>
    RecoveryRequired,

    /// <summary>The durable target committed but forward activation or cleanup still requires recovery.</summary>
    CommittedRecoveryRequired,

    /// <summary>The operation failed before mutation or could not establish a safe classified state.</summary>
    Failed,
}

/// <summary>Returns a typed mutation outcome and optional verified value.</summary>
/// <typeparam name="T">Type of the verified result value.</typeparam>
/// <param name="OperationId">Stable identifier of the completed operation.</param>
/// <param name="Outcome">Durable outcome classification.</param>
/// <param name="Value">Verified result value when available.</param>
/// <param name="ErrorCode">Stable diagnostic error code when the outcome is not successful.</param>
public sealed record MutationResult<T>(Guid OperationId, MutationOutcome Outcome, T? Value, string? ErrorCode);
