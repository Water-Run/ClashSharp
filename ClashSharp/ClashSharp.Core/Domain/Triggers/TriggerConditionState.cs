namespace ClashSharp.Model.Triggers;

/// <summary>Persistent state for one condition identity at one task revision.</summary>
/// <param name="IsArmed">Whether an edge condition may fire on an observed true value.</param>
/// <param name="ConsumedDate">Local date already consumed by a scheduled condition.</param>
/// <param name="ConsumedRevision">Task revision already consumed by an all-time condition.</param>
public sealed record TriggerConditionState(
    bool IsArmed = true,
    DateOnly? ConsumedDate = null,
    long? ConsumedRevision = null);
