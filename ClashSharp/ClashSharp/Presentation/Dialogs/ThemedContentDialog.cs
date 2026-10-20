using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>ContentDialog that inherits the current application root theme, text language and reading direction.</summary>
internal sealed class ThemedContentDialog : ContentDialog, ILocalizedPresentation
{
    private Action? _refreshText;

    /// <summary>Registers display-only updates; input values, selections and operation snapshots stay owned by the caller.</summary>
    public void Localize(Action refreshText)
    {
        ArgumentNullException.ThrowIfNull(refreshText);
        refreshText();
        _refreshText += refreshText;
    }

    public void RefreshLocalization() => _refreshText?.Invoke();

    public ThemedContentDialog()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            RequestedTheme = root.RequestedTheme;
            Language = root.Language;
            FlowDirection = root.FlowDirection;
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
