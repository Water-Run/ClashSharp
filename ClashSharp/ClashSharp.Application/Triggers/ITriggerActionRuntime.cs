using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Identifies the observed final state of one idempotent desired action effect.</summary>
public enum TriggerActionProbeStatus
{
    /// <summary>The desired effect is already verified.</summary>
    Desired = 0,

    /// <summary>The observed effect differs from the desired effect.</summary>
    NotDesired = 1,

    /// <summary>The final effect cannot currently be established.</summary>
    Unknown = 2,
}

/// <summary>Typed probe result used before dispatch and during crash reconciliation.</summary>
public sealed class TriggerActionProbeResult
{
    private TriggerActionProbeResult(TriggerActionProbeStatus status, string? diagnosticCode)
    {
        Status = status;
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the observed desired-state classification.</summary>
    public TriggerActionProbeStatus Status { get; }

    /// <summary>Gets a stable diagnostic code when the state is unknown.</summary>
    public string? DiagnosticCode { get; }

    /// <summary>Creates a verified desired-state result.</summary>
    public static TriggerActionProbeResult Desired() =>
        new(TriggerActionProbeStatus.Desired, null);

    /// <summary>Creates a verified different-state result.</summary>
    public static TriggerActionProbeResult NotDesired() =>
        new(TriggerActionProbeStatus.NotDesired, null);

    /// <summary>Creates an unknown-state result.</summary>
    public static TriggerActionProbeResult Unknown(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        return new TriggerActionProbeResult(TriggerActionProbeStatus.Unknown, diagnosticCode);
    }
}

/// <summary>Identifies the immediate outcome of applying one durable desired action.</summary>
public enum TriggerActionApplyStatus
{
    /// <summary>The operation returned and must be verified by a follow-up probe.</summary>
    Applied = 0,

    /// <summary>The operation failed conclusively before reaching the desired state.</summary>
    Failed = 1,

    /// <summary>The operation may have taken effect but cannot be established safely.</summary>
    Uncertain = 2,

    /// <summary>Exit ownership was durably handed to the outer lifetime protocol.</summary>
    HandedOff = 3,
}

/// <summary>Typed immediate action-application result.</summary>
public sealed class TriggerActionApplyResult
{
    private TriggerActionApplyResult(TriggerActionApplyStatus status, string? diagnosticCode)
    {
        Status = status;
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the immediate application classification.</summary>
    public TriggerActionApplyStatus Status { get; }

    /// <summary>Gets a stable failure or uncertainty diagnostic.</summary>
    public string? DiagnosticCode { get; }

    /// <summary>Creates an applied result that still requires verification.</summary>
    public static TriggerActionApplyResult Applied() =>
        new(TriggerActionApplyStatus.Applied, null);

    /// <summary>Creates a conclusive failed result.</summary>
    public static TriggerActionApplyResult Failed(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        return new TriggerActionApplyResult(TriggerActionApplyStatus.Failed, diagnosticCode);
    }

    /// <summary>Creates an uncertain result that must block later actions.</summary>
    public static TriggerActionApplyResult Uncertain(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        return new TriggerActionApplyResult(TriggerActionApplyStatus.Uncertain, diagnosticCode);
    }

    /// <summary>Creates a durable lifecycle-handoff result.</summary>
    public static TriggerActionApplyResult HandedOff() =>
        new(TriggerActionApplyStatus.HandedOff, null);
}

/// <summary>Platform boundary for idempotent probing and application of durable trigger actions.</summary>
public interface ITriggerActionRuntime
{
    /// <summary>Probes the desired final state, including notification idempotency identity.</summary>
    Task<TriggerActionProbeResult> ProbeAsync(
        TriggerOutboxAction action,
        CancellationToken cancellationToken);

    /// <summary>Applies an effect using the already-owned ordinary mutation admission lease.</summary>
    Task<TriggerActionApplyResult> ApplyAsync(
        TriggerOutboxAction action,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken);
}
