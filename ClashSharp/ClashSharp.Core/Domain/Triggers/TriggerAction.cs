namespace ClashSharp.Model.Triggers;

/// <summary>Base type for immutable typed trigger-action parameters.</summary>
public abstract record TriggerActionParameters;

/// <summary>Parameters for an action that accepts no value.</summary>
public sealed record NoActionParameters : TriggerActionParameters;

/// <summary>Parameters for an action that sets a Boolean state.</summary>
/// <param name="Value">Desired final state.</param>
public sealed record BooleanActionParameters(bool Value) : TriggerActionParameters;

/// <summary>Parameters for a proxy-mode action.</summary>
/// <param name="Mode">Desired primary proxy mode.</param>
public sealed record ProxyModeActionParameters(ClashSharpMode Mode) : TriggerActionParameters;

/// <summary>Parameters for a notification action.</summary>
/// <param name="Message">Nonempty notification message.</param>
public sealed record NotificationActionParameters(string Message) : TriggerActionParameters;

/// <summary>One immutable ordered trigger action with typed parameters.</summary>
/// <param name="Kind">Action effect kind.</param>
/// <param name="Parameters">Typed action parameters.</param>
public sealed record TriggerAction(
    TriggerActionKind Kind,
    TriggerActionParameters Parameters);
