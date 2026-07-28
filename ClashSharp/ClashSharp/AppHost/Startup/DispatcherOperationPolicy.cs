using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Hosting.Startup;

/// <summary>Runs an awaited dispatcher operation or an awaited same-thread fallback.</summary>
/// <remarks>
/// The caller owns thread affinity for the fallback. The dispatcher callback is <see langword="async void"/>
/// only because the dispatcher requires <see cref="Action"/>; its recoverable completion is always observed
/// through the returned task, while process-fatal failures remain unhandled at the dispatcher boundary.
/// </remarks>
internal static class DispatcherOperationPolicy
{
    /// <summary>Attempts one enqueue without containing cancellation or process-fatal failures.</summary>
    internal static bool TryEnqueue(
        Func<Action, bool> tryEnqueue,
        Action operation)
    {
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return tryEnqueue(operation);
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to enqueue the same operation within an explicit bound, allowing a transient
    /// dispatcher rejection to recover without duplicating an accepted operation.
    /// </summary>
    internal static async Task<bool> TryEnqueueWithRetryAsync(
        Func<Action, bool> tryEnqueue,
        Action operation,
        int maximumAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        if (retryDelay < TimeSpan.Zero || retryDelay == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryDelay),
                retryDelay,
                "The dispatcher retry delay must be finite and non-negative.");
        }

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryEnqueue(tryEnqueue, operation))
            {
                return true;
            }

            if (attempt < maximumAttempts && retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts bounded dispatcher recovery and otherwise awaits the caller-owned non-UI cleanup.
    /// </summary>
    internal static async Task<bool> RunWithRetryOrFallbackAsync(
        Func<Action, bool> tryEnqueue,
        Action operation,
        int maximumAttempts,
        TimeSpan retryDelay,
        Func<Task> fallbackOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fallbackOperation);
        bool accepted = await TryEnqueueWithRetryAsync(
            tryEnqueue,
            operation,
            maximumAttempts,
            retryDelay,
            cancellationToken).ConfigureAwait(false);
        if (accepted)
        {
            return true;
        }

        await fallbackOperation().ConfigureAwait(false);
        return false;
    }

    /// <summary>Schedules the primary operation, falling back synchronously when scheduling is rejected.</summary>
    internal static async Task RunOrFallbackAsync(
        Func<Action, bool> tryEnqueue,
        Func<Task> dispatchedOperation,
        Func<Task> fallbackOperation)
    {
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        ArgumentNullException.ThrowIfNull(dispatchedOperation);
        ArgumentNullException.ThrowIfNull(fallbackOperation);

        TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool accepted = TryEnqueue(
            tryEnqueue,
            () => RunDispatchedOperationAsync(dispatchedOperation, completion));

        if (!accepted)
        {
            await fallbackOperation().ConfigureAwait(false);
            return;
        }

        await completion.Task.ConfigureAwait(false);
    }

    private static async void RunDispatchedOperationAsync(
        Func<Task> operation,
        TaskCompletionSource completion)
    {
        try
        {
            await operation();
            completion.TrySetResult();
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            completion.TrySetException(exception);
        }
    }
}
