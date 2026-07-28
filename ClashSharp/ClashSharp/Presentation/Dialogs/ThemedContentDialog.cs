using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>ContentDialog that inherits the current application root theme.</summary>
internal sealed class ThemedContentDialog : ContentDialog
{
    public ThemedContentDialog()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            RequestedTheme = root.RequestedTheme;
        }
    }

    /// <summary>Shows this dialog through the per-window modal coordinator.</summary>
    /// <param name="cancellationToken">Dismisses the dialog when its owning visual lifetime ends.</param>
    /// <returns>The selected result, or <see cref="ContentDialogResult.None"/> when another dialog is active.</returns>
    public Task<ContentDialogResult> ShowManagedAsync(
        CancellationToken cancellationToken = default)
    {
        return WindowDialogCoordinator.ShowAsync(this, cancellationToken);
    }
}
