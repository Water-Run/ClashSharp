using System;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Hosting.Startup;

/// <summary>Holds the one pre-network startup conflict snapshot shown later by the window.</summary>
internal sealed class StartupConflictSnapshot
{
    public IReadOnlyList<StartupConflictIssue> Issues { get; private set; } = [];

    public bool ProbeFailed { get; private set; }

    public bool HasBlockingConflicts => ProbeFailed || Issues.Count > 0;

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
