using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Coordinates an admitted retained reset, runtime activation, compensation, and finalization.</summary>
/// <remarks>
/// This stateless coordinator preserves the caller's context for presentation participants.
/// Cancellation is accepted before durable work; every accepted decision then runs to completion.
/// </remarks>
public sealed class SettingsResetCoordinator
{
    /// <summary>Executes one reset while the caller holds exclusive mutation admission.</summary>
    /// <param name="operation">Storage and runtime ports for this admitted operation.</param>
    /// <param name="scope">Exactly All, Startup, Proxy, or TransparentProxy.</param>
    /// <param name="transparentProxyEnabled">Previously verified supported TUN default for a network group.</param>
    /// <param name="cancellationToken">Cancellation before the durable reset begins.</param>
    public async Task ExecuteAsync(
        ISettingsResetOperation operation,
        SettingsResetScope scope,
        bool transparentProxyEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (scope is not (SettingsResetScope.All or SettingsResetScope.Startup
            or SettingsResetScope.Proxy or SettingsResetScope.TransparentProxy))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        cancellationToken.ThrowIfCancellationRequested();
        SettingsRuntimeSnapshot baseline = operation.CaptureSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        IRetainedSettingsResetReceipt? receipt = null;
        Exception? operationFailure = null;
        try
        {
            try
            {
                receipt = operation.BeginReset(scope, transparentProxyEnabled)
                    ?? throw new InvalidOperationException("The settings reset transaction did not return a receipt.");
            }
            catch (Exception resetFailure) when (!ExceptionGraphClassifier.IsProcessFatal(resetFailure))
            {
                // A legacy callback can fail after modifying durable values. Converge to the
                // remaining authority rather than attempting an unowned rollback without a receipt.
                (SettingsRuntimeSnapshot partialCommit, Exception? convergenceFailure) =
                    await TryApplyCurrentAsync(operation, scope);
                if (convergenceFailure is not null)
                {
                    throw new SettingsResetRecoveryException(resetFailure, convergenceFailure);
                }

                operation.ReportApplied(partialCommit, scope, operationFailed: true);
                throw;
            }

            (SettingsRuntimeSnapshot defaults, Exception? activationFailure) =
                await TryApplyCurrentAsync(operation, scope);
            if (activationFailure is null)
            {
                List<Exception> failures = [];
                CaptureFailure(() => operation.ReportApplied(defaults, scope, operationFailed: false), failures);
                await CaptureFailureAsync(() => CompleteWithRetryAsync(receipt.CommitAsync), failures);
                ThrowCombinedFailures(failures, "The activated settings reset could not publish or finalize its commit decision.");
                return;
            }

            Exception? rollbackFailure = await TryRunAsync(() => CompleteWithRetryAsync(receipt.RollbackAsync));
            if (rollbackFailure is not null)
            {
                throw new SettingsResetRecoveryException(activationFailure, rollbackFailure);
            }

            Exception? compensationFailure = await TryRunAsync(() => RestoreAsync(operation, baseline, scope));
            if (compensationFailure is not null)
            {
                throw new SettingsResetRecoveryException(activationFailure, compensationFailure);
            }

            operation.ReportApplied(baseline, scope, operationFailed: true);
            ExceptionDispatchInfo.Capture(activationFailure).Throw();
            throw new UnreachableException();
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            if (receipt is not null)
            {
                await DisposeReceiptAsync(receipt, operationFailure);
            }
        }
    }

    private static async ValueTask DisposeReceiptAsync(IRetainedSettingsResetReceipt receipt, Exception? operationFailure)
    {
        try
        {
            await receipt.DisposeAsync();
        }
        catch (Exception disposalFailure) when (operationFailure is not null
            && !ExceptionGraphClassifier.IsProcessFatal(disposalFailure))
        {
            if (operationFailure is SettingsResetRecoveryException recoveryFailure)
            {
                throw new SettingsResetRecoveryException(
                    recoveryFailure.ActivationFailure,
                    new AggregateException(recoveryFailure.RecoveryFailure, disposalFailure));
            }

            throw new AggregateException(
                "Settings reset failed and its retained receipt could not be disposed.",
                operationFailure,
                disposalFailure);
        }
    }

    private static async Task CompleteWithRetryAsync(Func<CancellationToken, Task> complete)
    {
        try
        {
            await complete(CancellationToken.None);
        }
        catch (Exception firstFailure) when (!ExceptionGraphClassifier.IsProcessFatal(firstFailure))
        {
            try
            {
                await complete(CancellationToken.None);
            }
            catch (Exception retryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(retryFailure))
            {
                throw new AggregateException(
                    "The retained settings reset decision could not be finalized after retry.",
                    firstFailure,
                    retryFailure);
            }
        }
    }

    private static async Task<(SettingsRuntimeSnapshot Snapshot, Exception? Failure)> TryApplyCurrentAsync(
        ISettingsResetOperation operation,
        SettingsResetScope scope)
    {
        try
        {
            SettingsRuntimeSnapshot snapshot = operation.CaptureSnapshot();
            return (snapshot, await TryApplyAsync(operation, snapshot, scope));
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return (default, exception);
        }
    }

    private static async Task<Exception?> TryApplyAsync(
        ISettingsResetOperation operation,
        SettingsRuntimeSnapshot snapshot,
        SettingsResetScope scope)
    {
        List<Exception> failures = [];
        if (scope == SettingsResetScope.All)
        {
            CaptureFailure(() => operation.ApplyLanguage(snapshot.DisplayLanguage), failures);
            CaptureFailure(() => operation.ApplyTheme(snapshot.AppThemeMode), failures);
            CaptureFailure(() => operation.ApplyAccentColor(snapshot.AppAccentColorMode, snapshot.AppAccentColorValue), failures);
        }

        if (scope is SettingsResetScope.All or SettingsResetScope.Startup)
        {
            await CaptureFailureAsync(
                () => operation.ApplyLaunchAtStartupAsync(snapshot.LaunchAtStartupEnabled, CancellationToken.None), failures);
        }

        if (scope is SettingsResetScope.All or SettingsResetScope.Proxy)
        {
            await CaptureFailureAsync(() => operation.RestartConnectionSamplingAsync(CancellationToken.None), failures);
        }

        if (scope is SettingsResetScope.All or SettingsResetScope.Proxy or SettingsResetScope.TransparentProxy)
        {
            await CaptureFailureAsync(
                () => operation.ApplyNetworkSettingsAsync(snapshot.TransparentProxyEnabled, snapshot.MixedPort, CancellationToken.None), failures);
        }

        CaptureFailure(() =>
        {
            if (operation.CaptureSnapshot() != snapshot)
            {
                throw new InvalidOperationException(
                    "A settings reset participant did not preserve the durable external settings snapshot.");
            }
        }, failures);
        return CombineFailures(failures, "One or more settings reset participants failed to apply the durable state.");
    }

    private static async Task RestoreAsync(
        ISettingsResetOperation operation,
        SettingsRuntimeSnapshot baseline,
        SettingsResetScope scope)
    {
        List<Exception> failures = [];
        if (scope == SettingsResetScope.All)
        {
            CaptureFailure(() => operation.RestoreDurableSnapshot(baseline), failures);
        }

        SettingsRuntimeSnapshot durableTarget = operation.CaptureSnapshot();
        if (durableTarget != baseline)
        {
            failures.Add(new InvalidOperationException(
                "The retained reset receipt did not restore the previous durable settings."));
        }

        Exception? activationFailure = await TryApplyAsync(operation, durableTarget, scope);
        if (activationFailure is not null)
        {
            failures.Add(activationFailure);
        }

        ThrowCombinedFailures(failures, "Settings reset compensation did not restore every participant.");
    }

    private static void ThrowCombinedFailures(List<Exception> failures, string message)
    {
        Exception? failure = CombineFailures(failures, message);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static Exception? CombineFailures(List<Exception> failures, string message) => failures.Count switch
    {
        0 => null,
        1 => failures[0],
        _ => new AggregateException(message, failures),
    };

    private static void CaptureFailure(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }
    }

    private static async Task CaptureFailureAsync(Func<Task> action, ICollection<Exception> failures)
    {
        Exception? failure = await TryRunAsync(action);
        if (failure is not null)
        {
            failures.Add(failure);
        }
    }

    private static async Task<Exception?> TryRunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return exception;
        }
    }
}
