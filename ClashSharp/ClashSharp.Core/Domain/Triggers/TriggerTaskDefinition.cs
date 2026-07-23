using System.Collections.ObjectModel;

namespace ClashSharp.Model.Triggers;

/// <summary>Immutable revisioned trigger definition containing AND conditions and ordered actions.</summary>
/// <remarks>
/// Invariants: Callers validate instances with <see cref="TriggerDefinitionValidator"/> before persistence.
/// Thread safety: Immutable after construction.
/// Side effects: None.
/// </remarks>
public sealed class TriggerTaskDefinition
{
    /// <summary>Initializes one trigger definition and defensively copies its collections.</summary>
    /// <param name="id">Stable task identity.</param>
    /// <param name="revision">Positive definition revision.</param>
    /// <param name="name">User-visible task name.</param>
    /// <param name="isEnabled">Whether the task participates in evaluation.</param>
    /// <param name="conditions">Conditions combined with logical AND.</param>
    /// <param name="actions">Actions executed in order.</param>
    public TriggerTaskDefinition(
        string id,
        long revision,
        string name,
        bool isEnabled,
        IEnumerable<TriggerCondition> conditions,
        IEnumerable<TriggerAction> actions)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(actions);

        Id = id;
        Revision = revision;
        Name = name;
        IsEnabled = isEnabled;
        Conditions = Array.AsReadOnly(conditions.ToArray());
        Actions = Array.AsReadOnly(actions.ToArray());
    }

    /// <summary>Gets the stable task identity.</summary>
    public string Id { get; }

    /// <summary>Gets the positive definition revision.</summary>
    public long Revision { get; }

    /// <summary>Gets the user-visible task name.</summary>
    public string Name { get; }

    /// <summary>Gets whether the task participates in evaluation.</summary>
    public bool IsEnabled { get; }

    /// <summary>Gets the immutable conditions combined with logical AND.</summary>
    public ReadOnlyCollection<TriggerCondition> Conditions { get; }

    /// <summary>Gets the immutable ordered actions.</summary>
    public ReadOnlyCollection<TriggerAction> Actions { get; }
}
