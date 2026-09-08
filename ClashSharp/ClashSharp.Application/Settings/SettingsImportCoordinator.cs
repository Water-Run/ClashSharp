using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Owns retained import activation, compensation, and finalization through explicit ports.</summary>
/// <remarks>The caller owns exclusive admission and context until the returned task completes.</remarks>
public sealed class SettingsImportCoordinator
{
    /// <summary>Imports one package and completes its selected durable decision.</summary>
    /// <param name="operation">Already-admitted import, profile, and runtime ports.</param>
    /// <param name="packagePath">The package selected and validated by the presentation boundary.</param>
    /// <param name="cancellationToken">Cancellation before the retained import begins.</param>
    public async Task ExecuteAsync(ISettingsImportOperation operation, string packagePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        cancellationToken.ThrowIfCancellationRequested();
        SettingsRuntimeSnapshot baseline = operation.CaptureSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        IRetainedSettingsTransactionReceipt? receipt = null;
        Exception? operationFailure = null;
        try
        {
            receipt = await operation.BeginImportAsync(packagePath, cancellationToken)
                ?? throw new InvalidOperationException("The package import transaction did not return a receipt.");
            bool activationCompleted = false;
            try
            {
                operation.InvalidateProfiles();
                await ApplyAsync(operation, operation.CaptureSnapshot());
                activationCompleted = true;
                await CompleteWithRetryAsync(receipt.CommitAsync);
            }
            catch (Exception activationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(activationFailure))
            {
                if (activationCompleted)
                {
                    throw;
                }

                try
                {
                    await CompleteWithRetryAsync(receipt.RollbackAsync);
                    operation.InvalidateProfiles();
                    if (operation.CaptureSnapshot() != baseline)
                    {
                        throw new InvalidOperationException("The retained import receipt did not restore the previous durable settings.");
                    }

                    await ApplyAsync(operation, baseline);
                }
                catch (Exception recoveryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(recoveryFailure))
                {
                    throw new SettingsImportRecoveryException(activationFailure, recoveryFailure);
                }

                ExceptionDispatchInfo.Capture(activationFailure).Throw();
                throw;
            }
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

    private static async Task ApplyAsync(ISettingsRuntimeParticipants operation, SettingsRuntimeSnapshot snapshot)
    {
        Exception? failure = await SettingsRuntimeApplier.TryApplyAsync(operation, snapshot, SettingsResetScope.All);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task CompleteWithRetryAsync(Func<CancellationToken, Task> completion)
    {
        try
        {
            await completion(CancellationToken.None);
        }
        catch (Exception firstFailure) when (!ExceptionGraphClassifier.IsProcessFatal(firstFailure))
        {
            try
            {
                await completion(CancellationToken.None);
            }
            catch (Exception retryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(retryFailure))
            {
                throw new AggregateException(
                    "The retained data transaction completion could not be finalized after retry.", firstFailure, retryFailure);
            }
        }
    }

    private static async ValueTask DisposeReceiptAsync(IRetainedSettingsTransactionReceipt receipt, Exception? operationFailure)
    {
        try
        {
            await receipt.DisposeAsync();
        }
        catch (Exception disposalFailure) when (operationFailure is not null
            && !ExceptionGraphClassifier.IsProcessFatal(disposalFailure))
        {
            if (operationFailure is SettingsImportRecoveryException recoveryFailure)
            {
                throw new SettingsImportRecoveryException(
                    recoveryFailure.ActivationFailure,
                    new AggregateException(recoveryFailure.RecoveryFailure, disposalFailure));
            }

            throw new AggregateException(
                "Settings import failed and its retained receipt could not be disposed.", operationFailure, disposalFailure);
        }
    }
}
