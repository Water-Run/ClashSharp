using System.Collections.ObjectModel;

namespace ClashSharp.Model.Triggers;

/// <summary>Identifies the result of one pure trigger match transition.</summary>
public enum TriggerMatchOutcome
{
    /// <summary>The complete AND predicate did not match.</summary>
    NotMatched = 0,

    /// <summary>The complete AND predicate matched and its latches were consumed.</summary>
    Matched = 1,

    /// <summary>At least one required value was unavailable, so no execution may be created.</summary>
    InsufficientData = 2,
}

/// <summary>Immutable result of one pure trigger match transition.</summary>
public sealed class TriggerMatchDecision
{
    internal TriggerMatchDecision(
        TriggerMatchOutcome outcome,
        long expectedStateVersion,
        TriggerTaskState nextState,
        IEnumerable<string> unavailableConditionIds)
    {
        Outcome = outcome;
        ExpectedStateVersion = expectedStateVersion;
        NextState = nextState;
        UnavailableConditionIds = Array.AsReadOnly(unavailableConditionIds.ToArray());
    }

    /// <summary>Gets the match outcome.</summary>
    public TriggerMatchOutcome Outcome { get; }

    /// <summary>Gets the repository state version that must still be current at commit.</summary>
    public long ExpectedStateVersion { get; }

    /// <summary>Gets the complete proposed next latch state.</summary>
    public TriggerTaskState NextState { get; }

    /// <summary>Gets condition identities whose required data was unavailable.</summary>
    public ReadOnlyCollection<string> UnavailableConditionIds { get; }
}
