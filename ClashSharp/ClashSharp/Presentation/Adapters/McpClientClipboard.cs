using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Materializes and verifies explicitly requested private MCP clipboard content on its owning UI thread.</summary>
internal static class McpClientClipboard
{
    /// <summary>Copies usable text while excluding its bearer credential from clipboard history and cloud synchronization.</summary>
    /// <param name="configuration">Private client configuration; never logged or retained by this adapter.</param>
    /// <param name="dispatcher">The requesting page's UI dispatcher.</param>
    /// <param name="cancellationToken">Cancels a queued or incomplete copy when the page leaves.</param>
    /// <returns>True only when the clipboard can immediately supply the same nonempty text.</returns>
    public static async Task<bool> TryCopyAsync(string configuration, DispatcherQueue dispatcher, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentNullException.ThrowIfNull(dispatcher);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        CancellationToken token = deadline.Token;
        try
        {
            if (dispatcher.HasThreadAccess) { return await WriteAndVerifyAsync(configuration, token); }
            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try { completion.TrySetResult(await WriteAndVerifyAsync(configuration, token)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.TrySetCanceled(token); }
                catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    completion.TrySetException(failure);
                }
            })) { return false; }
            return await completion.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (COMException) { return false; }
    }

    private static Task<bool> WriteAndVerifyAsync(string configuration, CancellationToken cancellationToken) =>
        ClipboardTextWriter.WriteAndVerifyAsync(configuration, isPrivate: true, cancellationToken);
}
