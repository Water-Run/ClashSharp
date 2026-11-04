using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Writes text to the Windows clipboard, materializes it for other processes and confirms it by reading it back.</summary>
/// <remarks>
/// Thread safety: Must run on the owning UI thread, which the WinRT clipboard requires.
/// Native contract: Every clipboard change notifies listeners such as <c>rdpclip</c>, which in a connected Remote Desktop
/// session opens the clipboard for a few milliseconds to forward the new formats. A concurrent open fails with
/// <c>CLIPBRD_E_CANT_OPEN</c>; that single HRESULT is transient and retried with bounded backoff. Any other failure is
/// returned to the caller unchanged.
/// </remarks>
internal static class ClipboardTextWriter
{
    /// <summary><c>CLIPBRD_E_CANT_OPEN</c>: another process currently holds the clipboard open.</summary>
    internal const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    /// <summary>Upper bound for attempts of one clipboard step; the backoff totals about two seconds.</summary>
    internal const int MaximumAttempts = 10;

    /// <summary>Copies <paramref name="text"/> and reports success only when the clipboard returns the same text.</summary>
    /// <param name="text">Nonempty text to copy. Private text is never logged by this adapter.</param>
    /// <param name="isPrivate">Excludes the content from clipboard history and cloud synchronization when true.</param>
    /// <param name="cancellationToken">Cancels pending retries when the owning page leaves.</param>
    /// <returns>True when the same text can immediately be pasted by other processes.</returns>
    /// <exception cref="COMException">The clipboard failed with a non-transient error, or stayed locked for every attempt.</exception>
    public static async Task<bool> WriteAndVerifyAsync(string text, bool isPrivate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        DataPackage package = new() { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        ClipboardContentOptions options = new() { IsAllowedInHistory = !isPrivate, IsRoamable = !isPrivate };
        if (!await RetryWhileLockedAsync(() => SystemClipboard.SetContentWithOptions(package, options), cancellationToken))
        {
            return false;
        }

        // Render every format now so other processes, including RDP clients, read it without delayed rendering.
        await RetryWhileLockedAsync(static () =>
        {
            SystemClipboard.Flush();
            return true;
        }, cancellationToken);
        string copied = await RetryWhileLockedAsync(static async token =>
        {
            DataPackageView view = SystemClipboard.GetContent();
            return view.Contains(StandardDataFormats.Text) ? await view.GetTextAsync().AsTask(token) : string.Empty;
        }, cancellationToken);
        return copied.Length > 0
            && string.Equals(text.ReplaceLineEndings("\n"), copied.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>Runs one synchronous clipboard step, retrying only while another process holds the clipboard.</summary>
    private static Task<T> RetryWhileLockedAsync<T>(Func<T> step, CancellationToken cancellationToken) =>
        RetryWhileLockedAsync(_ => Task.FromResult(step()), cancellationToken);

    /// <summary>Runs one asynchronous clipboard step, retrying only while another process holds the clipboard.</summary>
    private static async Task<T> RetryWhileLockedAsync<T>(Func<CancellationToken, Task<T>> step, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await step(cancellationToken);
            }
            catch (COMException exception) when (exception.HResult == ClipboardCannotOpen && attempt < MaximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt), cancellationToken);
            }
        }
    }
}
