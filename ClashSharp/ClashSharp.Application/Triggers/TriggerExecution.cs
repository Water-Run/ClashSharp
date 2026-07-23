namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Immutable durable trigger execution metadata.</summary>
public sealed class TriggerExecution
{
    /// <summary>Initializes one durable execution.</summary>
    /// <param name="executionId">Nonempty execution identity.</param>
    /// <param name="taskId">Stable source task identity.</param>
    /// <param name="taskRevision">Positive source definition revision.</param>
    /// <param name="triggeredAt">Timestamp at which matching committed.</param>
    /// <param name="processEpoch">Nonempty process epoch that created the execution.</param>
    /// <param name="state">Aggregate execution state.</param>
    public TriggerExecution(
        Guid executionId,
        string taskId,
        long taskRevision,
        DateTimeOffset triggeredAt,
        Guid processEpoch,
        TriggerExecutionState state)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("Execution identity must be nonempty.", nameof(executionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(taskRevision);
        if (processEpoch == Guid.Empty)
        {
            throw new ArgumentException("Process epoch must be nonempty.", nameof(processEpoch));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ExecutionId = executionId;
        TaskId = taskId;
        TaskRevision = taskRevision;
        TriggeredAt = triggeredAt;
        ProcessEpoch = processEpoch;
        State = state;
    }

    /// <summary>Gets the execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the source task identity.</summary>
    public string TaskId { get; }

    /// <summary>Gets the source definition revision.</summary>
    public long TaskRevision { get; }

    /// <summary>Gets the timestamp at which matching committed.</summary>
    public DateTimeOffset TriggeredAt { get; }

    /// <summary>Gets the process epoch that created the execution.</summary>
    public Guid ProcessEpoch { get; }

    /// <summary>Gets aggregate execution state.</summary>
    public TriggerExecutionState State { get; }
}
