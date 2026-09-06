using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.Presentation.Lifecycle;

/// <summary>Cancels superseded reads and owns every admitted read until it has finished.</summary>
internal sealed class PageLoadSession
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<CancellationTokenSource, Task> _loads = [];
    private CancellationTokenSource? _activeLoad;
    private readonly IApplicationErrorSink? _errorSink;
    private readonly PageOperationSession? _cancellationErrors;
    private readonly string _operationName;

    /// <summary>Creates a read session with optional observation of unexpected platform-boundary errors.</summary>
    public PageLoadSession(IApplicationErrorSink? errorSink = null, string operationName = "page-load")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        _errorSink = errorSink;
        _operationName = operationName;
        _cancellationErrors = errorSink is null
            ? null
            : new PageOperationSession(errorSink, operationName + "-cancel");
    }

    /// <summary>Runs a page load and cancels any older load still owned by this session.</summary>
    /// <param name="loadAsync">Cancellable page-load operation. Must not be null.</param>
    /// <param name="debounceDelay">Optional cancellable delay before invoking the load.</param>
    /// <param name="cancellationToken">Optional enclosing page or dialog cancellation.</param>
    /// <returns>A task that represents this load invocation.</returns>
    public Task RunAsync(
        Func<CancellationToken, Task> loadAsync,
        TimeSpan debounceDelay = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadAsync);
        if (debounceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(debounceDelay),
                debounceDelay,
                "Debounce delay must not be negative.");
        }

        lock (_syncRoot)
        {
            CancellationTokenSource currentLoad =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationTokenSource? previousLoad = _activeLoad;
            _activeLoad = currentLoad;
            Task task = ExecuteAsync(loadAsync, debounceDelay, currentLoad, previousLoad);
            _loads.Add(currentLoad, task);
            return task;
        }
    }

    private async Task ExecuteAsync(
        Func<CancellationToken, Task> loadAsync,
        TimeSpan debounceDelay,
        CancellationTokenSource currentLoad,
        CancellationTokenSource? previousLoad)
    {
        // Admission records ownership before any callback can run or finish synchronously.
        await Task.Yield();
        try
        {
            try
            {
                TryCancel(previousLoad);
            }
            catch (Exception exception) when (_errorSink is not null
                && !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                await ReportUnexpectedAsync(exception);
            }

            currentLoad.Token.ThrowIfCancellationRequested();
            if (debounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(debounceDelay, currentLoad.Token);
            }

            await loadAsync(currentLoad.Token);
        }
        catch (Exception exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, currentLoad.Token))
        {
        }
        catch (Exception exception) when (_errorSink is not null
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportUnexpectedAsync(exception);
        }
        finally
        {
            currentLoad.Dispose();
            lock (_syncRoot)
            {
                _loads.Remove(currentLoad);
                if (ReferenceEquals(_activeLoad, currentLoad))
                {
                    _activeLoad = null;
                }
            }
        }
    }

    /// <summary>Cancels all admitted reads, including replaced reads still unwinding.</summary>
    public void Cancel()
    {
        CancellationTokenSource[] loads;
        lock (_syncRoot)
        {
            loads = [.. _loads.Keys];
            _activeLoad = null;
        }

        List<Exception> unobserved = [];
        foreach (CancellationTokenSource load in loads)
        {
            try
            {
                TryCancel(load);
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                if (_cancellationErrors is not null)
                {
                    // The error queue owns this task and is included in DrainAsync.
                    _cancellationErrors.RunAsync(_ => Task.FromException(exception));
                }
                else
                {
                    unobserved.Add(exception);
                }
            }
        }
        if (unobserved.Count > 0)
        {
            throw new AggregateException(unobserved);
        }
    }

    /// <summary>Waits for all reads and cancellation error reports admitted before this call.</summary>
    public Task DrainAsync()
    {
        lock (_syncRoot)
        {
            return Task.WhenAll([
                .. _loads.Values,
                _cancellationErrors?.DrainAsync() ?? Task.CompletedTask,
            ]);
        }
    }

    private async Task ReportUnexpectedAsync(Exception exception)
    {
        try
        {
            await _errorSink!.ReportAsync(
                new ApplicationError(_operationName, exception),
                CancellationToken.None);
        }
        catch (Exception sinkException) when (!ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
            // Error presentation cannot strand the owned operation.
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The replaced load completed between ownership transfer and cancellation.
        }
    }
}
