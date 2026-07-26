namespace ClashSharp.ApplicationModel.Data;

/// <summary>Identifies the in-process lifecycle state of a data-generation scope.</summary>
public enum DataGenerationScopeState
{
    /// <summary>The scope is prepared but invisible to ordinary operations.</summary>
    Staged,

    /// <summary>The scope accepts pinned ordinary operations.</summary>
    Active,

    /// <summary>The scope rejects new leases while existing work drains.</summary>
    Draining,

    /// <summary>The scope is no longer authoritative and awaits disposal.</summary>
    Retired,

    /// <summary>Owned resources are being disposed.</summary>
    Disposing,

    /// <summary>Owned resources were disposed successfully.</summary>
    Disposed,

    /// <summary>Owned resource disposal failed and admission must remain closed.</summary>
    DisposalFailed,
}
