using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.Presentation.Lifecycle;

/// <summary>Owns an ordered queue of page actions, including cancellation and error observation.</summary>
/// <remarks>
/// Unlike a latest-wins read session, admitting another action never cancels an accepted action.
/// Each invocation owns its cancellation source until its delegate exits. Cancel also revokes
/// queued actions; a later page visit waits for the previous queue before entering new work.
/// Delegates retain the caller's synchronization context so platform interaction stays on the UI thread.
/// </remarks>
internal sealed class PageOperationSession
{
    private readonly object _syncRoot = new();
    private readonly HashSet<CancellationTokenSource> _operations = [];
    private readonly IApplicationErrorSink _errorSink;
    private readonly string _operationName;
    private Task _tail = Task.CompletedTask;

    public PageOperationSession(IApplicationErrorSink errorSink, string operationName)
    {
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        _operationName = operationName;
    }

    /// <summary>Queues an action after all earlier actions, without replacing their lifetime.</summary>
    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_syncRoot)
        {
            EnqueueUnderLock(operation, cancellationToken);
            return _tail;
        }
    }

    // Admission owns _syncRoot; every task returned by ExecuteAsync remains owned by _tail.
    private void EnqueueUnderLock(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operations.Add(cancellation);
        _tail = ExecuteAsync(_tail, operation, cancellation);
    }

    /// <summary>Cancels all accepted actions; their own completion paths release their resources.</summary>
    public void Cancel()
    {
        CancellationTokenSource[] operations;
        lock (_syncRoot)
        {
            operations = [.. _operations];
        }

        foreach (CancellationTokenSource cancellation in operations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The action finished after the snapshot and before cancellation reached it.
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                // A failing cancellation callback is reported by an owned queue entry, while
                // cancellation continues to reach the remaining accepted actions.
                lock (_syncRoot)
                {
                    EnqueueUnderLock(_ => Task.FromException(exception));
                }
            }
        }
    }

    /// <summary>Observes completion of every action accepted before this call.</summary>
    public Task DrainAsync()
    {
        lock (_syncRoot)
        {
            return _tail;
        }
    }

    private async Task ExecuteAsync(
        Task predecessor,
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation)
    {
        // Never run application callbacks while admission still holds the queue's lock.
        await Task.Yield();
        try
        {
            await predecessor;
            cancellation.Token.ThrowIfCancellationRequested();
            await operation(cancellation.Token);
        }
        catch (Exception exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellation.Token))
        {
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            try
            {
                await _errorSink.ReportAsync(
                    new ApplicationError(_operationName, exception),
                    CancellationToken.None);
            }
            catch (Exception sinkException) when (!ExceptionGraphClassifier.IsProcessFatal(sinkException))
            {
                // An unavailable error presenter must not strand this queue or fault an async-void UI event.
            }
        }
        finally
        {
            cancellation.Dispose();
            lock (_syncRoot)
            {
                _operations.Remove(cancellation);
                if (_operations.Count == 0)
                {
                    // Idle page sessions must not retain completed delegate/Task chains.
                    _tail = Task.CompletedTask;
                }
            }
        }
    }
}
