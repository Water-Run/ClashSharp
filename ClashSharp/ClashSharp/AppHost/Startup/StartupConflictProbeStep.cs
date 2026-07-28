using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Probes startup conflicts once before any configured mode transition starts the owned core.</summary>
internal sealed class StartupConflictProbeStep(
    AppSettingsService settings,
    StartupConflictDetectionService conflicts,
    StartupConflictSnapshot snapshot,
    LogStorageService logStorage,
    LocalizationService localization) : IStartupStep
{
    public string Name => "startup-conflict-probe";

    public int Order => 425;

    public async Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.StartupConflictCheckEnabled)
        {
            snapshot.Capture([]);
            return StartupStepResult.Succeeded();
        }

        try
        {
            int mixedPort = settings.MixedPort;
            IReadOnlyList<StartupConflictIssue> issues =
                await conflicts
                    .CheckConflictsAsync(mixedPort, cancellationToken)
                    .ConfigureAwait(false);
            snapshot.Capture(issues);
            return StartupStepResult.Succeeded();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            snapshot.CaptureFailure();
            logStorage.AppendLog(
                "Warning",
                "Startup",
                localization.GetString("Startup.Log.ConflictDialogSkipped"),
                exception.Message);
            return StartupStepResult.Warning("startup-conflict-probe-failed");
        }
    }
}
