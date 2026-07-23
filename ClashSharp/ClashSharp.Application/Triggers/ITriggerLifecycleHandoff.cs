using System.Globalization;
using ClashSharp.ApplicationModel.Lifecycle;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Stable identity of one trigger exit handoff in one process epoch.</summary>
public sealed record TriggerLifecycleHandoffIdentity
{
    /// <summary>Initializes a validated execution/action/process identity.</summary>
    public TriggerLifecycleHandoffIdentity(Guid executionId, int actionIndex, Guid processEpoch)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("Execution identity must be nonempty.", nameof(executionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(actionIndex);
        if (processEpoch == Guid.Empty)
        {
            throw new ArgumentException("Process epoch must be nonempty.", nameof(processEpoch));
        }

        ExecutionId = executionId;
        ActionIndex = actionIndex;
        ProcessEpoch = processEpoch;
    }

    /// <summary>Gets the owning trigger execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the ordered ExitApplication action index.</summary>
    public int ActionIndex { get; }

    /// <summary>Gets the process epoch that owns the handoff.</summary>
    public Guid ProcessEpoch { get; }

    /// <summary>Creates the cross-layer idempotency key used by the outer lifetime channel.</summary>
    public static string CreateKey(Guid executionId, int actionIndex, Guid processEpoch)
    {
        _ = new TriggerLifecycleHandoffIdentity(executionId, actionIndex, processEpoch);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"trigger-exit:{executionId:N}:{actionIndex}:{processEpoch:N}");
    }

    /// <summary>Creates this identity's cross-layer idempotency key.</summary>
    public string CreateKey() => CreateKey(ExecutionId, ActionIndex, ProcessEpoch);
}

/// <summary>Coordinates durable exit publication, epoch recovery, and producer release acknowledgement.</summary>
public interface ITriggerLifecycleHandoff
{
    /// <summary>Probes completion and safely finalizes handoffs owned by a prior process epoch.</summary>
    Task<TriggerActionProbeResult> ProbeAsync(
        TriggerOutboxAction action,
        CancellationToken cancellationToken);

    /// <summary>Atomically inserts the durable handoff and publishes a non-owned exit request.</summary>
    Task<TriggerActionApplyResult> HandOffAsync(
        TriggerOutboxAction action,
        CancellationToken cancellationToken);

    /// <summary>Persists release only after every trigger-owned lease has unwound.</summary>
    Task AcknowledgeReleaseAsync(
        TriggerLifecycleHandoffIdentity identity,
        CancellationToken cancellationToken);

    /// <summary>Finds and acknowledges this execution's handed-off exit after its outer work lease releases.</summary>
    Task AcknowledgeReleasedExecutionAsync(
        TriggerExecution execution,
        CancellationToken cancellationToken);
}
