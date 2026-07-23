namespace ClashSharp.Model.Triggers;

/// <summary>One immutable trigger condition with a stable identity and typed parameters.</summary>
/// <param name="Id">Stable condition identity within its task.</param>
/// <param name="Kind">Condition parameter shape.</param>
/// <param name="Parameters">Typed condition parameters.</param>
public sealed record TriggerCondition(
    string Id,
    TriggerConditionKind Kind,
    TriggerConditionParameters Parameters);
