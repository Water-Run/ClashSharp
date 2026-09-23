using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Shows centered diagnostic content using the window's native modal dialog.</summary>
/// <remarks>
/// Native presentation owns theme resources, Escape, focus containment and window resizing.
/// The window coordinator owns admission and cancellation for this visual lifetime.
/// </remarks>
internal static class CenteredDialogOverlay
{
    /// <summary>Shows scrollable content until the user closes it or its owner is canceled.</summary>
    public static async Task ShowAsync(
        XamlRoot xamlRoot,
        string title,
        UIElement content,
        string closeButtonText,
        double dialogWidth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        ScrollViewer body = content as ScrollViewer ?? new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        ThemedContentDialog dialog = new()
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = body,
            CloseButtonText = closeButtonText,
            MinWidth = 0,
        };
        dialog.Resources["ContentDialogMinWidth"] = 320d;
        dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;

        void UpdateBounds(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            body.MaxHeight = Math.Max(120, xamlRoot.Size.Height - 240);
        }

        body.MaxHeight = Math.Max(120, xamlRoot.Size.Height - 240);
        xamlRoot.Changed += UpdateBounds;
        try
        {
            await dialog.ShowManagedAsync(cancellationToken);
        }
        finally
        {
            xamlRoot.Changed -= UpdateBounds;
        }
    }
}
