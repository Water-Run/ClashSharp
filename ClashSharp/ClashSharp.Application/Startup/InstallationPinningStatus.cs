namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Observed outcome of an explicitly selected installation taskbar request.</summary>
public enum InstallationPinningStatus
{
    /// <summary>No matching installation request remains.</summary>
    NoRequest,
    /// <summary>The request remains pending until a foreground attempt can run.</summary>
    Deferred,
    /// <summary>The platform can offer its native confirmation.</summary>
    Ready,
    /// <summary>The application is already pinned; no confirmation is needed.</summary>
    AlreadyPinned,
    /// <summary>The native confirmation completed with the application pinned.</summary>
    Pinned,
    /// <summary>Windows returned false, including a user declining confirmation.</summary>
    NotPinned,
    /// <summary>The native confirmation is unavailable or prohibited.</summary>
    Unavailable,
}
