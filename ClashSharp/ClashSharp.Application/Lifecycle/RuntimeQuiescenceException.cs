namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Retains the exact resume state when a participant cannot complete an already-started pause.</summary>
/// <remarks>The lifecycle owner retains this participant's recovery obligation alongside earlier paused participants.</remarks>
public sealed class RuntimeQuiescenceException : Exception
{
    /// <summary>Initializes a failed pause with its restoration state and original failure.</summary>
    /// <param name="priorState">State captured before the participant began changing its scheduling.</param>
    /// <param name="failure">Original cancellation or recoverable failure; its type and token remain authoritative.</param>
    public RuntimeQuiescenceException(QuiescedState priorState, Exception failure)
        : base("The runtime participant could not complete its pause.", failure ?? throw new ArgumentNullException(nameof(failure)))
    {
        PriorState = priorState ?? throw new ArgumentNullException(nameof(priorState));
        Failure = failure;
    }

    /// <summary>Gets the opaque state to pass back to the same participant when compensating the pause.</summary>
    public QuiescedState PriorState { get; }

    /// <summary>Gets the original failure without replacing its identity, cancellation token or fatal causes.</summary>
    public Exception Failure { get; }
}
