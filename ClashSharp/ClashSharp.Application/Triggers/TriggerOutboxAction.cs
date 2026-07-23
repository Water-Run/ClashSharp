using System.Globalization;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Creates deterministic action idempotency keys.</summary>
public static class TriggerIdempotencyKey
{
    /// <summary>Creates a stable key from execution identity, task revision, and ordered action index.</summary>
    /// <param name="executionId">Nonempty execution identity.</param>
    /// <param name="taskRevision">Positive task revision.</param>
    /// <param name="actionIndex">Nonnegative ordered action index.</param>
    /// <returns>Invariant lowercase key.</returns>
    public static string Create(Guid executionId, long taskRevision, int actionIndex)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("Execution identity must be nonempty.", nameof(executionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(taskRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(actionIndex);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{executionId:N}:{taskRevision}:{actionIndex}");
    }
}

/// <summary>Immutable durable outbox action.</summary>
public sealed class TriggerOutboxAction
{
    /// <summary>Initializes one durable outbox action.</summary>
    /// <param name="executionId">Owning execution identity.</param>
    /// <param name="taskRevision">Positive source task revision.</param>
    /// <param name="actionIndex">Nonnegative ordered action index.</param>
    /// <param name="idempotencyKey">Stable effect idempotency key.</param>
    /// <param name="desiredEffect">Typed desired effect.</param>
    /// <param name="state">Durable reconciliation state.</param>
    /// <param name="attemptCount">Nonnegative execution-attempt count.</param>
    /// <param name="lastError">Latest stable error detail, or null.</param>
    public TriggerOutboxAction(
        Guid executionId,
        long taskRevision,
        int actionIndex,
        string idempotencyKey,
        TriggerAction desiredEffect,
        TriggerOutboxState state,
        int attemptCount = 0,
        string? lastError = null)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("Execution identity must be nonempty.", nameof(executionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(taskRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(actionIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (!StringComparer.Ordinal.Equals(
                idempotencyKey,
                TriggerIdempotencyKey.Create(executionId, taskRevision, actionIndex)))
        {
            throw new ArgumentException(
                "Idempotency key must be derived from the execution, revision, and action index.",
                nameof(idempotencyKey));
        }

        ArgumentNullException.ThrowIfNull(desiredEffect);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
        ExecutionId = executionId;
        TaskRevision = taskRevision;
        ActionIndex = actionIndex;
        IdempotencyKey = idempotencyKey;
        DesiredEffect = desiredEffect;
        State = state;
        AttemptCount = attemptCount;
        LastError = lastError;
    }

    /// <summary>Gets the owning execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the source task revision.</summary>
    public long TaskRevision { get; }

    /// <summary>Gets the ordered action index.</summary>
    public int ActionIndex { get; }

    /// <summary>Gets the stable effect idempotency key.</summary>
    public string IdempotencyKey { get; }

    /// <summary>Gets the typed desired effect.</summary>
    public TriggerAction DesiredEffect { get; }

    /// <summary>Gets durable reconciliation state.</summary>
    public TriggerOutboxState State { get; }

    /// <summary>Gets the execution-attempt count.</summary>
    public int AttemptCount { get; }

    /// <summary>Gets latest stable error detail, or null.</summary>
    public string? LastError { get; }
}

/// <summary>Optimistic request to transition one durable outbox action.</summary>
public sealed record TriggerOutboxTransition
{
    /// <summary>Initializes one validated outbox transition.</summary>
    /// <param name="executionId">Owning execution identity.</param>
    /// <param name="actionIndex">Ordered action index.</param>
    /// <param name="expectedState">State that must still be authoritative.</param>
    /// <param name="nextState">Requested next state.</param>
    /// <param name="lastError">Latest stable error detail, or null.</param>
    public TriggerOutboxTransition(
        Guid executionId,
        int actionIndex,
        TriggerOutboxState expectedState,
        TriggerOutboxState nextState,
        string? lastError = null)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("Execution identity must be nonempty.", nameof(executionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(actionIndex);
        if (!Enum.IsDefined(expectedState))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedState));
        }

        if (!Enum.IsDefined(nextState))
        {
            throw new ArgumentOutOfRangeException(nameof(nextState));
        }

        ExecutionId = executionId;
        ActionIndex = actionIndex;
        ExpectedState = expectedState;
        NextState = nextState;
        LastError = lastError;
    }

    /// <summary>Gets the owning execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the ordered action index.</summary>
    public int ActionIndex { get; }

    /// <summary>Gets the state that must still be authoritative.</summary>
    public TriggerOutboxState ExpectedState { get; }

    /// <summary>Gets the requested next state.</summary>
    public TriggerOutboxState NextState { get; }

    /// <summary>Gets the latest stable error detail, or null.</summary>
    public string? LastError { get; }
}
