/*
 * Centered Dialog Overlay
 * Hosts modal app dialogs in a root-panel overlay so the dialog card is centered reliably
 *
 * @author: WaterRun
 * @file: View/CenteredDialogOverlay.cs
 * @date: 2026-06-28
 */

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.View;

/// <summary>Shows modal dialog content in an app-owned centered overlay.</summary>
/// <remarks>
/// Invariants: The overlay child always matches the main window root size.
/// Thread safety: Must be called from the UI thread.
/// Side effects: Adds and removes a modal-looking root visual that consumes pointer input until closed.
/// </remarks>
internal static class CenteredDialogOverlay
{
    /// <summary>Shows centered modal content and completes when the close button or popup close event fires.</summary>
    public static Task ShowAsync(XamlRoot xamlRoot, string title, UIElement content, string closeButtonText, double dialogWidth)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(content);

        if (App.MainWindow?.Content is not Panel rootPanel)
        {
            throw new InvalidOperationException("The main window content root must be a Panel to host centered overlays.");
        }

        TaskCompletionSource<object?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Button closeButton = BuildCloseButton(closeButtonText);
        Grid overlay = BuildOverlay(xamlRoot, title, content, closeButton, dialogWidth);
        Grid.SetRow(overlay, 0);
        if (rootPanel is Grid rootGrid)
        {
            Grid.SetRowSpan(overlay, Math.Max(1, rootGrid.RowDefinitions.Count));
        }
        Canvas.SetZIndex(overlay, 10_000);

        void CloseOverlay()
        {
            rootPanel.Children.Remove(overlay);
            closed.TrySetResult(null);
        }

        closeButton.Click += (_, _) =>
        {
            CloseOverlay();
        };

        rootPanel.Children.Add(overlay);
        closeButton.Focus(FocusState.Programmatic);
        return closed.Task;
    }

    private static Grid BuildOverlay(XamlRoot xamlRoot, string title, UIElement content, Button closeButton, double dialogWidth)
    {
        (double Width, double Height) overlaySize = ResolveOverlaySize(xamlRoot);
        Grid overlay = new()
        {
            Width = overlaySize.Width,
            Height = overlaySize.Height,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 0, 0, 0)),
        };

        Border card = BuildCard(overlaySize.Width, overlaySize.Height, dialogWidth);
        Grid layout = new()
        {
            RowSpacing = 18,
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock titleBlock = new()
        {
            Text = title,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            Foreground = ResourceBrush("TextFillColorPrimaryBrush", new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))),
            TextWrapping = TextWrapping.Wrap,
        };
        layout.Children.Add(titleBlock);

        FrameworkElement body = content is ScrollViewer scrollViewer
            ? scrollViewer
            : new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(160, overlaySize.Height - 260),
            };
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        Grid buttonRow = new();
        buttonRow.Children.Add(closeButton);
        Grid.SetRow(buttonRow, 2);
        layout.Children.Add(buttonRow);

        card.Child = layout;
        overlay.Children.Add(card);
        return overlay;
    }

    private static Border BuildCard(double overlayWidth, double overlayHeight, double dialogWidth)
    {
        double constrainedWidth = Math.Min(dialogWidth, Math.Max(320, overlayWidth - 48));
        return new Border
        {
            Width = constrainedWidth,
            MaxHeight = Math.Max(320, overlayHeight - 96),
            Padding = new Thickness(28, 24, 28, 24),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ResourceBrush("ControlStrokeColorDefaultBrush", new SolidColorBrush(Windows.UI.Color.FromArgb(255, 105, 105, 105))),
            Background = ResourceBrush("SolidBackgroundFillColorBaseBrush", new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static (double Width, double Height) ResolveOverlaySize(XamlRoot xamlRoot)
    {
        if (App.MainWindow?.Content is FrameworkElement root &&
            root.ActualWidth > 0 &&
            root.ActualHeight > 0)
        {
            return (root.ActualWidth, root.ActualHeight);
        }

        return (xamlRoot.Size.Width, xamlRoot.Size.Height);
    }

    private static Button BuildCloseButton(string closeButtonText)
    {
        return new Button
        {
            Content = closeButtonText,
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
    }

    private static Brush ResourceBrush(string key, Brush fallback)
    {
        return Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
            ? brush
            : fallback;
    }
}
