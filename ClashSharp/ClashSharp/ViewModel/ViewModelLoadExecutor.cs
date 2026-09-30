using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.ViewModel;

/// <summary>Runs cancellable view-model reads away from the UI thread and applies successful snapshots.</summary>
internal static class ViewModelLoadExecutor
{
    /// <summary>Reads one snapshot in the background and applies it after cancellation is rechecked.</summary>
    /// <typeparam name="TSnapshot">Immutable or isolated snapshot type.</typeparam>
    /// <param name="readSnapshot">Synchronous snapshot reader. Must not be null.</param>
    /// <param name="applySnapshot">Snapshot application callback. Must not be null.</param>
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <param name="operationName">Stable diagnostic operation name.</param>
    /// <param name="cancellationToken">Cancels this load attempt.</param>
    /// <param name="isCurrent">Optional caller-owned revision check before publishing data or errors.</param>
    /// <returns>True only when the current snapshot was applied successfully.</returns>
    public static async Task<bool> ExecuteAsync<TSnapshot>(
        Func<TSnapshot> readSnapshot,
        Action<TSnapshot> applySnapshot,
        IApplicationErrorSink errorSink,
        string operationName,
        CancellationToken cancellationToken,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(applySnapshot);
        ArgumentNullException.ThrowIfNull(errorSink);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        try
        {
            TSnapshot snapshot = await Task.Run(readSnapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (isCurrent?.Invoke() == false) { return false; }
            applySnapshot(snapshot);
            return true;
        }
        catch (OperationCanceledException exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            if (!cancellationToken.IsCancellationRequested && isCurrent?.Invoke() != false)
            {
                await ReportUnexpectedAsync(errorSink, operationName, exception);
            }
        }

        return false;
    }

    private static async Task ReportUnexpectedAsync(
        IApplicationErrorSink errorSink,
        string operationName,
        Exception exception)
    {
        try
        {
            await errorSink.ReportAsync(
                new ApplicationError(operationName, exception),
                CancellationToken.None);
        }
        catch (Exception sinkException) when (
            !ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
            // The primary failure remains represented by the unchanged safe view-model state.
        }
    }
}
