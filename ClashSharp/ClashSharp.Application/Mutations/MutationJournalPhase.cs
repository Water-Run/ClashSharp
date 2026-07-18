namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Identifies the latest durable phase boundary of a mutation operation.</summary>
public enum MutationJournalPhase
{
    /// <summary>The mutation plan is durable but no participant has staged external state.</summary>
    Planned,

    /// <summary>A participant is staging temporary state.</summary>
    Staging,

    /// <summary>A participant is applying an external side effect.</summary>
    Applying,

    /// <summary>The coordinator is verifying observed external state.</summary>
    Verifying,

    /// <summary>The durable target was promoted but the commit marker is not yet durable.</summary>
    TargetPromoted,

    /// <summary>The point-of-no-return commit marker is durable.</summary>
    Committed,

    /// <summary>The coordinator is restoring the verified baseline.</summary>
    Compensating,

    /// <summary>A retained operation is being recovered.</summary>
    Recovering,

    /// <summary>Committed activation or cleanup is being completed.</summary>
    CleaningUp,
}

/// <summary>Classifies state observed by an idempotent mutation participant probe.</summary>
public enum MutationProbeState
{
    /// <summary>The participant matches the verified baseline.</summary>
    Baseline,

    /// <summary>The participant matches the verified desired target.</summary>
    Desired,

    /// <summary>The participant has a known mixture that can be compensated safely.</summary>
    Partial,

    /// <summary>The participant state cannot be classified safely.</summary>
    Unknown,
}
