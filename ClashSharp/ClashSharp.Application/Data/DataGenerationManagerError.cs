namespace ClashSharp.ApplicationModel.Data;

/// <summary>Identifies a stable generation-manager admission or transition failure.</summary>
public enum DataGenerationManagerError
{
    /// <summary>No verified current generation has been initialized.</summary>
    NotInitialized,

    /// <summary>New operations are rejected because the current generation is draining.</summary>
    Draining,

    /// <summary>The caller supplied a stale current-manifest identity.</summary>
    StaleGeneration,

    /// <summary>The staged scope is duplicate, stale, skipped, or otherwise invalid.</summary>
    InvalidStage,

    /// <summary>The requested transition is invalid at its current durable boundary.</summary>
    InvalidTransition,

    /// <summary>An owned generation scope could not be disposed safely.</summary>
    ScopeDisposalFailed,

    /// <summary>Durable manifest promotion completed and the transition is forward-only until restored.</summary>
    ManifestPromotionCommitted,

    /// <summary>The durable manifest could not be classified as baseline or staged candidate.</summary>
    ManifestPromotionUncertain,

    /// <summary>Durable baseline restoration completed and only in-memory cleanup remains.</summary>
    ManifestRestorationCommitted,

    /// <summary>The durable restoration result could not be classified safely.</summary>
    ManifestRestorationUncertain,

    /// <summary>The manager is disposing or already disposed.</summary>
    Disposed,
}
