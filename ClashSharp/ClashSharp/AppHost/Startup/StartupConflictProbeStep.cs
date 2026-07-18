using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

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

/// <summary>Probes startup conflicts once before any configured mode transition starts the owned core.</summary>
internal sealed class StartupConflictProbeStep(
    AppSettingsService settings,
    StartupConflictDetectionService conflicts,
    StartupConflictSnapshot snapshot) : IStartupStep
{
    public string Name => "startup-conflict-probe";

    public int Order => 425;

    public Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.StartupConflictCheckEnabled)
        {
            snapshot.Capture([]);
            return Task.FromResult(StartupStepResult.Succeeded());
        }

        try
        {
            snapshot.Capture(conflicts.CheckConflicts(settings.MixedPort));
            return Task.FromResult(StartupStepResult.Succeeded());
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            snapshot.CaptureFailure();
            LogStorageService.Instance.AppendLog(
                "Warning",
                "Startup",
                LocalizationService.Instance.GetString("Startup.Log.ConflictDialogSkipped"),
                exception.Message);
            return Task.FromResult(StartupStepResult.Warning("startup-conflict-probe-failed"));
        }
    }
}
