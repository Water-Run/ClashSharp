namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Immutable durable lifecycle handoff for one trigger outbox action.</summary>
public sealed class TriggerLifecycleHandoff
{
    /// <summary>Initializes one lifecycle handoff.</summary>
    /// <param name="executionId">Owning execution identity.</param>
    /// <param name="actionIndex">Ordered ExitApplication action index.</param>
    /// <param name="processEpoch">Process epoch that created the handoff.</param>
    /// <param name="state">Durable handoff state.</param>
    /// <param name="updatedAt">Timestamp of the latest transition.</param>
    /// <param name="lastError">Latest stable error detail, or null.</param>
    public TriggerLifecycleHandoff(
        Guid executionId,
        int actionIndex,
        Guid processEpoch,
        TriggerLifecycleHandoffState state,
        DateTimeOffset updatedAt,
        string? lastError)
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

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ExecutionId = executionId;
        ActionIndex = actionIndex;
        ProcessEpoch = processEpoch;
        State = state;
        UpdatedAt = updatedAt;
        LastError = lastError;
    }

    /// <summary>Gets the owning execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the ordered ExitApplication action index.</summary>
    public int ActionIndex { get; }

    /// <summary>Gets the process epoch that created the handoff.</summary>
    public Guid ProcessEpoch { get; }

    /// <summary>Gets durable handoff state.</summary>
    public TriggerLifecycleHandoffState State { get; }

    /// <summary>Gets the timestamp of the latest transition.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Gets latest stable error detail, or null.</summary>
    public string? LastError { get; }
}

/// <summary>Optimistic request to insert or transition one lifecycle handoff.</summary>
public sealed record TriggerLifecycleHandoffTransition
{
    /// <summary>Initializes one validated lifecycle handoff transition.</summary>
    /// <param name="executionId">Owning execution identity.</param>
    /// <param name="actionIndex">Ordered ExitApplication action index.</param>
    /// <param name="processEpoch">Process epoch that created the handoff.</param>
    /// <param name="expectedState">State that must still be authoritative, or null for insertion.</param>
    /// <param name="nextState">Requested next handoff state.</param>
    /// <param name="updatedAt">Timestamp of the transition.</param>
    /// <param name="lastError">Latest stable error detail, or null.</param>
    public TriggerLifecycleHandoffTransition(
        Guid executionId,
        int actionIndex,
        Guid processEpoch,
        TriggerLifecycleHandoffState? expectedState,
        TriggerLifecycleHandoffState nextState,
        DateTimeOffset updatedAt,
        string? lastError = null)
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

        if (expectedState is not null && !Enum.IsDefined(expectedState.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedState));
        }

        if (!Enum.IsDefined(nextState))
        {
            throw new ArgumentOutOfRangeException(nameof(nextState));
        }

        ExecutionId = executionId;
        ActionIndex = actionIndex;
        ProcessEpoch = processEpoch;
        ExpectedState = expectedState;
        NextState = nextState;
        UpdatedAt = updatedAt;
        LastError = lastError;
    }

    /// <summary>Gets the owning execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the ordered ExitApplication action index.</summary>
    public int ActionIndex { get; }

    /// <summary>Gets the process epoch that created the handoff.</summary>
    public Guid ProcessEpoch { get; }

    /// <summary>Gets the state that must still be authoritative, or null for insertion.</summary>
    public TriggerLifecycleHandoffState? ExpectedState { get; }

    /// <summary>Gets the requested next handoff state.</summary>
    public TriggerLifecycleHandoffState NextState { get; }

    /// <summary>Gets the timestamp of the transition.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Gets the latest stable error detail, or null.</summary>
    public string? LastError { get; }
}
