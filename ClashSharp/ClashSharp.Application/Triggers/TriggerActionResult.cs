namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Immutable result of one durable outbox action processing pass.</summary>
public sealed class TriggerActionResult
{
    /// <summary>Initializes one terminal action result.</summary>
    public TriggerActionResult(
        TriggerOutboxAction action,
        TriggerOutboxState finalState,
        string? diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (finalState is not (
            TriggerOutboxState.Succeeded or
            TriggerOutboxState.Failed or
            TriggerOutboxState.Uncertain or
            TriggerOutboxState.HandedOff))
        {
            throw new ArgumentOutOfRangeException(nameof(finalState));
        }

        if (action.State != finalState)
        {
            throw new ArgumentException("The action row must already contain the reported final state.", nameof(action));
        }

        bool needsDiagnostic = finalState is TriggerOutboxState.Failed or TriggerOutboxState.Uncertain;
        if (needsDiagnostic != !string.IsNullOrWhiteSpace(diagnosticCode))
        {
            throw new ArgumentException("Failure and uncertainty results require one stable diagnostic code.");
        }

        Action = action;
        FinalState = finalState;
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the final durable action row.</summary>
    public TriggerOutboxAction Action { get; }

    /// <summary>Gets the final durable state reached by this processing pass.</summary>
    public TriggerOutboxState FinalState { get; }

    /// <summary>Gets the stable failure or uncertainty diagnostic.</summary>
    public string? DiagnosticCode { get; }
}
