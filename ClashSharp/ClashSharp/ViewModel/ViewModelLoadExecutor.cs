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
    /// <returns>A task that completes after application, cancellation, or failure reporting.</returns>
    public static async Task ExecuteAsync<TSnapshot>(
        Func<TSnapshot> readSnapshot,
        Action<TSnapshot> applySnapshot,
        IApplicationErrorSink errorSink,
        string operationName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(applySnapshot);
        ArgumentNullException.ThrowIfNull(errorSink);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        try
        {
            TSnapshot snapshot = await Task.Run(readSnapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            applySnapshot(snapshot);
        }
        catch (OperationCanceledException exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportUnexpectedAsync(errorSink, operationName, exception);
        }
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
