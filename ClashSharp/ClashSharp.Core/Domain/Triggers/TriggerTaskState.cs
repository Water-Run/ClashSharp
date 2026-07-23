using System.Collections.ObjectModel;

namespace ClashSharp.Model.Triggers;

/// <summary>Immutable persistent latch state for one trigger task.</summary>
public sealed class TriggerTaskState
{
    /// <summary>Initializes a task state and defensively copies condition state.</summary>
    /// <param name="taskId">Stable task identity.</param>
    /// <param name="taskRevision">Definition revision represented by the state.</param>
    /// <param name="version">Repository state version used for optimistic commit.</param>
    /// <param name="conditionStates">State keyed by stable condition identity.</param>
    /// <param name="lastTriggeredAt">Latest known successful trigger timestamp for history.</param>
    public TriggerTaskState(
        string taskId,
        long taskRevision,
        long version,
        IReadOnlyDictionary<string, TriggerConditionState> conditionStates,
        DateTimeOffset? lastTriggeredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(conditionStates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(taskRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(version);

        if (conditionStates.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Condition state keys and values must be present.", nameof(conditionStates));
        }

        TaskId = taskId;
        TaskRevision = taskRevision;
        Version = version;
        ConditionStates = new ReadOnlyDictionary<string, TriggerConditionState>(
            new Dictionary<string, TriggerConditionState>(conditionStates, StringComparer.Ordinal));
        LastTriggeredAt = lastTriggeredAt;
    }

    /// <summary>Gets the stable task identity.</summary>
    public string TaskId { get; }

    /// <summary>Gets the definition revision represented by this state.</summary>
    public long TaskRevision { get; }

    /// <summary>Gets the repository state version used for optimistic commit.</summary>
    public long Version { get; }

    /// <summary>Gets immutable condition state keyed by stable condition identity.</summary>
    public ReadOnlyDictionary<string, TriggerConditionState> ConditionStates { get; }

    /// <summary>Gets the latest known successful trigger timestamp for display and migration history.</summary>
    public DateTimeOffset? LastTriggeredAt { get; }

    /// <summary>Creates armed initial state for every condition in a definition.</summary>
    /// <param name="definition">Definition whose task and condition identities are copied.</param>
    /// <param name="version">Existing repository state version, or zero for a new task.</param>
    /// <param name="lastTriggeredAt">Latest known successful trigger timestamp, if any.</param>
    /// <returns>Fresh state for the supplied definition revision.</returns>
    public static TriggerTaskState CreateInitial(
        TriggerTaskDefinition definition,
        long version = 0,
        DateTimeOffset? lastTriggeredAt = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Dictionary<string, TriggerConditionState> conditionStates = new(StringComparer.Ordinal);
        foreach (TriggerCondition? condition in definition.Conditions)
        {
            if (condition is not null && !conditionStates.ContainsKey(condition.Id))
            {
                conditionStates.Add(condition.Id, new TriggerConditionState());
            }
        }

        return new TriggerTaskState(
            definition.Id,
            definition.Revision,
            version,
            conditionStates,
            lastTriggeredAt);
    }
}
