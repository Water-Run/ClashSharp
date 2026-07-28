using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Presentation.Lifecycle;

/// <summary>Owns the cancellation lifetime of a page's latest explicit load operation.</summary>
internal sealed class PageLoadSession
{
    private readonly object _syncRoot = new();

    private CancellationTokenSource? _activeLoad;

    /// <summary>Runs a page load and cancels any older load still owned by this session.</summary>
    /// <param name="loadAsync">Cancellable page-load operation. Must not be null.</param>
    /// <param name="debounceDelay">Optional cancellable delay before invoking the load.</param>
    /// <returns>A task that represents this load invocation.</returns>
    public async Task RunAsync(
        Func<CancellationToken, Task> loadAsync,
        TimeSpan debounceDelay = default)
    {
        ArgumentNullException.ThrowIfNull(loadAsync);
        if (debounceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(debounceDelay),
                debounceDelay,
                "Debounce delay must not be negative.");
        }

        CancellationTokenSource currentLoad = new();
        CancellationTokenSource? previousLoad;
        lock (_syncRoot)
        {
            previousLoad = _activeLoad;
            _activeLoad = currentLoad;
        }
        TryCancel(previousLoad);

        try
        {
            if (debounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(debounceDelay, currentLoad.Token);
            }

            await loadAsync(currentLoad.Token);
        }
        catch (OperationCanceledException) when (
            currentLoad.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_activeLoad, currentLoad))
                {
                    _activeLoad = null;
                }
            }

            currentLoad.Dispose();
        }
    }

    /// <summary>Cancels the currently owned page load, if any.</summary>
    public void Cancel()
    {
        CancellationTokenSource? activeLoad;
        lock (_syncRoot)
        {
            activeLoad = _activeLoad;
            _activeLoad = null;
        }
        TryCancel(activeLoad);
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
