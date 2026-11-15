using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Coordinates modal UI so one window never presents overlapping dialogs.</summary>
/// <remarks>
/// Invariants: Presentation is scoped by <see cref="XamlRoot"/> and duplicate attempts are rejected immediately.
/// Thread safety: Admission is thread-safe; supplied UI work must still execute on the owning UI thread.
/// Side effects: Shows the supplied dialog or overlay for an admitted caller.
/// </remarks>
internal static class WindowDialogCoordinator
{
    private static readonly ConditionalWeakTable<XamlRoot, DialogPresentationGate> Gates = new();
    private static readonly ConditionalWeakTable<XamlRoot, OwnedDialogHost> Hosts = new();

    /// <summary>Reports an active owned dialog without accessing a XAML root from a background caller.</summary>
    internal static bool IsDialogForeground => OwnedDialogHost.IsForeground;

    /// <summary>Shows a content dialog when no other modal surface owns its visual root.</summary>
    /// <param name="dialog">Configured, unparented dialog; its native host supplies the presentation root.</param>
    /// <param name="xamlRoot">Application root owning admission, theme, and the native modal window.</param>
    /// <param name="cancellationToken">Dismisses the dialog when its owning visual lifetime ends.</param>
    /// <returns>The dialog result, or <see cref="ContentDialogResult.None"/> when another modal surface is active.</returns>
    public static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        XamlRoot xamlRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(xamlRoot);
        cancellationToken.ThrowIfCancellationRequested();
        DialogPresentationGate gate = Gates.GetValue(xamlRoot, static _ => new DialogPresentationGate());
        if (!gate.TryEnter(out IDisposable? lease))
        {
            return ContentDialogResult.None;
        }

        using (lease)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Native dialog content is outside the page tree and must follow its root for the entire presentation.
            using DialogRootPresentationBinding presentation = new(dialog, xamlRoot.Content as FrameworkElement, gate);
            using OwnedDialogHost host = OwnedDialogHost.Create(dialog, xamlRoot);
            XamlRoot hostedRoot = host.DialogRoot;
            Gates.Add(hostedRoot, gate);
            Hosts.Add(xamlRoot, host);
            try
            {
                host.Show();
                return await ShowWithCancellationAsync(dialog, host, gate, cancellationToken);
            }
            catch (OperationCanceledException) when (host.OwnerClosing.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ContentDialogResult.None;
            }
            finally
            {
                Hosts.Remove(xamlRoot);
                Gates.Remove(hostedRoot);
            }
        }
    }

    /// <summary>Fences queued cancellation callbacks before the owning native window is destroyed.</summary>
    /// <param name="xamlRoot">The owning visual root, if it has been initialized.</param>
    public static void MarkWindowClosing(XamlRoot? xamlRoot)
    {
        if (xamlRoot is not null)
        {
            if (Hosts.TryGetValue(xamlRoot, out OwnedDialogHost? host)) { host.DismissForOwnerClose(); }
            Gates.GetValue(xamlRoot, static _ => new DialogPresentationGate()).MarkWindowClosing();
        }
    }

    /// <summary>Refreshes an admitted dialog's controls only while its native window remains usable.</summary>
    public static bool TryRefreshDialogPresentation(XamlRoot xamlRoot, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        return Gates.TryGetValue(xamlRoot, out DialogPresentationGate? gate) && gate.TryRefreshPresentation(refresh);
    }

    /// <summary>Closes a page-owned flyout while the native window is still usable.</summary>
    public static bool TryDismissTransient(XamlRoot? xamlRoot, Action dismiss)
    {
        return xamlRoot is not null
            && Gates.GetValue(xamlRoot, static _ => new DialogPresentationGate()).TryDismissTransient(dismiss);
    }

    /// <summary>Shows a custom modal overlay when no other modal surface owns the visual root.</summary>
    /// <param name="xamlRoot">Visual root receiving the overlay.</param>
    /// <param name="showAsync">UI operation that owns the overlay until it closes.</param>
    /// <param name="cancellationToken">Dismisses the overlay when its owning visual lifetime ends.</param>
    /// <returns>True when the overlay was presented; false when another modal surface is active.</returns>
    public static async Task<bool> TryShowOverlayAsync(
        XamlRoot xamlRoot,
        Func<CancellationToken, Task> showAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(showAsync);
        cancellationToken.ThrowIfCancellationRequested();
        DialogPresentationGate gate = Gates.GetValue(xamlRoot, static _ => new DialogPresentationGate());
        if (!gate.TryEnter(out IDisposable? lease))
        {
            return false;
        }

        using (lease)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await showAsync(cancellationToken);
            return true;
        }
    }

    private static async Task<ContentDialogResult> ShowWithCancellationAsync(
        ContentDialog dialog,
        OwnedDialogHost host,
        DialogPresentationGate gate,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await ShowCoreAsync(dialog, host);
        }

        Task<ContentDialogResult> showTask = ShowCoreAsync(dialog, host);
        TaskCompletionSource<bool> cancellationSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellationSignal);

        Task completedTask = await Task.WhenAny(showTask, cancellationSignal.Task);
        if (ReferenceEquals(completedTask, showTask))
        {
            return await showTask;
        }

        try
        {
            if (!showTask.IsCompleted)
            {
                // Cancellation may resume after Closed; a pending WinRT task does not prove the HWND is alive.
                gate.TryDismiss(() => dialog.Hide());
            }
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            // Window teardown can complete the WinRT operation before Hide reaches the dialog.
        }

        try
        {
            _ = await showTask;
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            // Cancellation owns the result; window teardown may also fault the WinRT operation.
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static Task<ContentDialogResult> ShowCoreAsync(ContentDialog dialog, OwnedDialogHost host)
    {
        IAsyncOperation<ContentDialogResult> operation = dialog.ShowAsync(ContentDialogPlacement.InPlace);
        return operation.AsTask(host.OwnerClosing);
    }
}
