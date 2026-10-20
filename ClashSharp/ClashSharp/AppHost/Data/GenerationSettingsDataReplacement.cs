using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;

namespace ClashSharp.Hosting.Data;

/// <summary>Connects page intent to the owned replacement and its process-wide recovery state.</summary>
internal sealed class GenerationSettingsDataReplacement(
    GenerationReplacementCoordinator coordinator,
    Action requireRestart,
    Func<string, bool> requestRestart) : ISettingsDataReplacement
{
    public async Task<IPreparedSettingsImport> PrepareImportAsync(string packagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedImport(this, await coordinator.PrepareImportAsync(packagePath, cancellationToken).ConfigureAwait(false));
    }

    public Task<SettingsDataReplacementResult> ImportAsync(IPreparedSettingsImport import, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(import);
        cancellationToken.ThrowIfCancellationRequested();
        if (import is not PreparedImport prepared || !ReferenceEquals(prepared.Owner, this))
        {
            throw new ArgumentException("Import preview belongs to another replacement service.", nameof(import));
        }
        PreparedGenerationImport contents = prepared.Consume();
        return ExecuteAsync(() => coordinator.ImportPreparedAsync(contents, cancellationToken), "settings-import-recovery");
    }

    public Task<SettingsDataReplacementResult> ResetAllSettingsAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => coordinator.ResetAllSettingsAsync(cancellationToken), "settings-reset-recovery");

    private sealed class PreparedImport(GenerationSettingsDataReplacement owner, PreparedGenerationImport contents) : IPreparedSettingsImport
    {
        private PreparedGenerationImport? _contents = contents;
        public GenerationSettingsDataReplacement Owner { get; } = owner;
        public ClashDataPackageScope Scope { get; } = contents.Plan.Scope;

        public PreparedGenerationImport Consume() => Interlocked.Exchange(ref _contents, null)
            ?? throw new InvalidOperationException("Import preview was already consumed or abandoned; prepare and confirm again.");

        public void Dispose() => Interlocked.Exchange(ref _contents, null);
    }

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
