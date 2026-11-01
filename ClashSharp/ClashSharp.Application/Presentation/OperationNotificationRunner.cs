using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Reports completed operations once; cancellation and no-op results remain quiet.</summary>
public sealed class OperationNotificationRunner(IUserOperationNotifications? notifications)
{
    /// <summary>Reports a completed operation without a result value.</summary>
    /// <param name="operation">Complete asynchronous operation.</param>
    /// <param name="titleResourceKey">Localized category key.</param>
    /// <param name="successResourceKey">Localized success key.</param>
    /// <param name="failureResourceKey">Localized failure key.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public Task ExecuteAsync(Func<Task> operation, string titleResourceKey, string successResourceKey,
        string failureResourceKey, CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, titleResourceKey, successResourceKey, failureResourceKey, cancellationToken);

    /// <summary>Reports a completed synchronous operation without changing its result or error.</summary>
    /// <typeparam name="T">Operation result.</typeparam>
    /// <param name="operation">Complete synchronous operation.</param>
    /// <param name="titleResourceKey">Localized category key.</param>
    /// <param name="successResourceKey">Localized success key.</param>
    /// <param name="failureResourceKey">Localized failure key.</param>
    public T Execute<T>(Func<T> operation, string titleResourceKey, string successResourceKey, string failureResourceKey)
    {
        ArgumentNullException.ThrowIfNull(operation);
        T result;
        try { result = operation(); }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            ReportFailure(titleResourceKey, failureResourceKey, exception);
            throw;
        }
        if (result is not null) { TryReport(titleResourceKey, result is false ? failureResourceKey : successResourceKey, result is false); }
        return result;
    }

    /// <summary>Reports a completed synchronous operation.</summary>
    /// <param name="operation">Complete synchronous operation.</param>
    /// <param name="titleResourceKey">Localized category key.</param>
    /// <param name="successResourceKey">Localized success key.</param>
    /// <param name="failureResourceKey">Localized failure key.</param>
    public void Execute(Action operation, string titleResourceKey, string successResourceKey, string failureResourceKey) =>
        Execute(() => { operation(); return true; }, titleResourceKey, successResourceKey, failureResourceKey);

    /// <summary>Awaits the complete operation before reporting its result under the user's policy.</summary>
    /// <typeparam name="T">Detached operation result.</typeparam>
    /// <param name="operation">Complete operation, including its durable commit.</param>
    /// <param name="titleResourceKey">Localized category key.</param>
    /// <param name="successResourceKey">Localized successful outcome key.</param>
    /// <param name="failureResourceKey">Localized failed outcome key.</param>
    /// <param name="cancellationToken">Caller cancellation; cancellation does not produce an error toast.</param>
    /// <param name="classifyResult">Optional result policy; null means no completed operation to report.</param>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string titleResourceKey,
        string successResourceKey, string failureResourceKey, CancellationToken cancellationToken,
        Func<T, bool?>? classifyResult = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        T result;
        try { result = await operation().ConfigureAwait(false); }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            ReportFailure(titleResourceKey, failureResourceKey, exception);
            throw;
        }
        bool? succeeded = classifyResult is null ? result is null ? null : result is not false : classifyResult(result);
        if (succeeded is { } completed) { TryReport(titleResourceKey, completed ? successResourceKey : failureResourceKey, !completed); }
        return result;
    }

    private void TryReport(string title, string message, bool failed)
    {
        try { notifications?.ReportOperation(title, message, failed); }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            // A notification transport failure cannot undo a committed operation or replace its error.
        }
    }

    private void ReportFailure(string title, string message, Exception exception)
    {
        if (exception is not (SettingsDataReplacementRecoveryException or SettingsImportRecoveryException or SettingsResetRecoveryException))
        {
            TryReport(title, message, true);
            return;
        }
        try { notifications?.ReportAvailability(title, "Settings.DataReplacement.RecoveryRequired.Description", true); }
        catch (Exception deliveryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(deliveryFailure))
        {
            // Preserve the original durable recovery failure if the notification platform is unavailable.
        }
    }
}
