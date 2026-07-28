using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    /// <summary>Shows a content dialog when no other modal surface owns its visual root.</summary>
    /// <param name="dialog">Configured dialog with a non-null XAML root.</param>
    /// <param name="cancellationToken">Dismisses the dialog when its owning visual lifetime ends.</param>
    /// <returns>The dialog result, or <see cref="ContentDialogResult.None"/> when another modal surface is active.</returns>
    public static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        cancellationToken.ThrowIfCancellationRequested();
        XamlRoot xamlRoot = dialog.XamlRoot
            ?? throw new InvalidOperationException("A content dialog requires a XamlRoot before it can be shown.");
        DialogPresentationGate gate = Gates.GetValue(xamlRoot, static _ => new DialogPresentationGate());
        if (!gate.TryEnter(out IDisposable? lease))
        {
            return ContentDialogResult.None;
        }

        using (lease)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ShowWithCancellationAsync(dialog, cancellationToken);
        }
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
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await dialog.ShowAsync();
        }

        Task<ContentDialogResult> showTask = ShowCoreAsync(dialog);
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
                dialog.Hide();
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

    private static async Task<ContentDialogResult> ShowCoreAsync(ContentDialog dialog)
    {
        return await dialog.ShowAsync();
    }
}
