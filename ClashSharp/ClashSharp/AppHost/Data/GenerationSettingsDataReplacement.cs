using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Connects page intent to the owned replacement and its process-wide recovery state.</summary>
internal sealed class GenerationSettingsDataReplacement(
    GenerationReplacementCoordinator coordinator,
    Action requireRestart,
    Func<string, bool> requestRestart) : ISettingsDataReplacement
{
    public Task<SettingsDataReplacementResult> ImportAsync(string packagePath, CancellationToken cancellationToken) =>
        ExecuteAsync(() => coordinator.ImportAsync(packagePath, cancellationToken), "settings-import-recovery");

    public Task<SettingsDataReplacementResult> ResetAllSettingsAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => coordinator.ResetAllSettingsAsync(cancellationToken), "settings-reset-recovery");

    private async Task<SettingsDataReplacementResult> ExecuteAsync(
        Func<Task<GenerationReplacementResult>> replace, string recoveryReason)
    {
        GenerationReplacementResult result;
        try { result = await replace(); }
        catch (GenerationReplacementRecoveryException failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            List<Exception> failures = [failure];
            try { requireRestart(); }
            catch (Exception notificationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(notificationFailure))
            {
                failures.Add(notificationFailure);
            }
            try
            {
                if (!requestRestart(recoveryReason))
                {
                    failures.Add(new InvalidOperationException("The required data-recovery restart was rejected."));
                }
            }
            catch (Exception restartFailure) when (!ExceptionGraphClassifier.IsProcessFatal(restartFailure))
            {
                failures.Add(restartFailure);
            }
            throw new SettingsDataReplacementRecoveryException(failure.IsCommitted, failures);
        }

        List<string> warnings = result.Warnings.ToList();
        if (result.RequiresRestart)
        {
            try { requireRestart(); }
            catch (Exception notificationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(notificationFailure))
            {
                // A UI observer cannot turn a verified commit into a reported import/reset failure.
                warnings.Add("data.replacement.restart_notification_failed");
            }
        }
        return new(result.RequiresRestart, warnings.AsReadOnly());
    }
}
