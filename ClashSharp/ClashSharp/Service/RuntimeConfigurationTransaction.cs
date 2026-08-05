using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Applies and verifies one promoted mihomo runtime configuration generation.</summary>
internal interface ICoreConfigurationRuntime
{
    /// <summary>Starts or reloads the runtime with the promoted managed configuration.</summary>
    Task ApplyAsync(
        CoreConfigurationState configuration,
        long generation,
        RuntimeConfigurationActivationPlan plan,
        CancellationToken cancellationToken);

    /// <summary>Waits until the requested generation and configuration hash are serving through their authenticated controller.</summary>
    Task<bool> WaitUntilReadyAsync(
        long generation,
        string configurationHash,
        RuntimeConfigurationActivationPlan plan,
        CancellationToken cancellationToken);

    /// <summary>Commits network-facing side effects only after owner readiness is verified.</summary>
    Task CommitAsync(
        long generation,
        RuntimeConfigurationActivationPlan plan,
        CancellationToken cancellationToken);

    /// <summary>Stops the runtime when rollback has no previously applied configuration.</summary>
    Task DeactivateAsync(CancellationToken cancellationToken);
}

/// <summary>Captures the immutable owner and listener plan associated with one configuration generation.</summary>
internal sealed record RuntimeConfigurationActivationPlan(
    ClashSharpMode Mode,
    bool TunEnabled,
    int MixedPort,
    string ProfileId);

/// <summary>Identifies the durable desired and last verified runtime configuration generations.</summary>
internal sealed record RuntimeConfigurationGenerationState(
    long DesiredGeneration,
    string? DesiredContentHash,
    RuntimeConfigurationActivationPlan? DesiredPlan,
    long? AppliedGeneration,
    string? AppliedContentHash,
    RuntimeConfigurationActivationPlan? AppliedPlan)
{
    /// <summary>Gets whether the desired configuration is also the last readiness-verified configuration.</summary>
    public bool IsConverged => DesiredGeneration == AppliedGeneration
        && StringComparer.Ordinal.Equals(DesiredContentHash, AppliedContentHash)
        && DesiredPlan == AppliedPlan;
}

/// <summary>Describes the terminal state of one runtime configuration transaction.</summary>
internal enum RuntimeConfigurationTransactionOutcome
{
    /// <summary>The candidate passed validation, activation, and readiness and became applied.</summary>
    Applied,

    /// <summary>The candidate was rejected before promotion, leaving the applied generation unchanged.</summary>
    Rejected,

    /// <summary>Candidate activation failed and both file and runtime state were restored.</summary>
    RolledBack,

    /// <summary>Candidate activation failed and the previous runtime state could not be confirmed.</summary>
    RollbackFailed,
}

/// <summary>Reports desired/applied generations and rollback health for one configuration transaction.</summary>
internal sealed record RuntimeConfigurationTransactionResult(
    RuntimeConfigurationTransactionOutcome Outcome,
    RuntimeConfigurationGenerationState GenerationState,
    CoreConfigurationState Configuration,
    Exception? Failure,
    Exception? RollbackFailure)
{
    /// <summary>Gets a non-fatal bounded-retention cleanup failure after a successful publish.</summary>
    public Exception? MaintenanceFailure { get; init; }

    /// <summary>Gets whether the requested generation became the verified applied generation.</summary>
    public bool IsApplied => Outcome == RuntimeConfigurationTransactionOutcome.Applied;

    /// <summary>Gets whether desired state differs from the last verified applied state.</summary>
    public bool IsDegraded => !GenerationState.IsConverged;
}

/// <summary>Combines source-profile import and live runtime application as one rollback boundary.</summary>
internal sealed record ProfileRuntimeConfigurationTransactionResult(
    ProfileImportResult Profile,
    RuntimeConfigurationTransactionResult Runtime)
{
    /// <summary>Gets whether both the imported source and derived runtime candidate were committed.</summary>
    public bool IsApplied => Runtime.IsApplied;

    /// <summary>Gets a non-fatal cleanup failure after the combined transaction committed.</summary>
    public Exception? MaintenanceFailure { get; init; }
}
