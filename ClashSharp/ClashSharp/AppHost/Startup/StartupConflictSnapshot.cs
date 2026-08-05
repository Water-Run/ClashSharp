using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.Model;

namespace ClashSharp.Hosting.Startup;

/// <summary>Holds the one pre-network startup conflict snapshot shown later by the window.</summary>
internal sealed class StartupConflictSnapshot
{
    public IReadOnlyList<StartupConflictIssue> Issues { get; private set; } = [];

    public bool ProbeFailed { get; private set; }

    /// <summary>
    /// Returns whether the captured conditions must prevent the requested startup transition.
    /// Third-party TUN interfaces are advisory when Clash# is not itself about to acquire TUN;
    /// all other conflicts and an incomplete probe remain fail-closed.
    /// </summary>
    public bool HasBlockingConflicts(bool tunRequested)
    {
        return ProbeFailed
            || Issues.Any(issue =>
                issue.Kind != StartupConflictKind.ActiveTunInterface || tunRequested);
    }

    public void Capture(IReadOnlyList<StartupConflictIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues;
        ProbeFailed = false;
    }

    public void CaptureFailure()
    {
        Issues = [];
        ProbeFailed = true;
    }
}
