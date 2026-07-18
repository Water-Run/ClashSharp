namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Identifies process-level launch control flow.</summary>
public enum ApplicationLaunchDisposition
{
    /// <summary>The primary host started and remains attached to the process lifetime.</summary>
    Running,

    /// <summary>Activation was redirected and the secondary process must exit.</summary>
    Redirected,

    /// <summary>A primary helper path completed and the process must exit.</summary>
    ExitRequested,

    /// <summary>Primary startup reported a typed fatal outcome and the process must exit.</summary>
    Fatal,
}

/// <summary>Returns process launch disposition and the underlying startup result.</summary>
/// <param name="Disposition">Process-level launch disposition.</param>
/// <param name="StartupResult">Primary startup outcome; null for redirected activation.</param>
public sealed record ApplicationLaunchResult(
    ApplicationLaunchDisposition Disposition,
    StartupStepResult? StartupResult);
