using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Shows modal dialog content in an app-owned centered overlay.</summary>
/// <remarks>
/// Invariants: The overlay child always matches the main window root size.
/// Thread safety: Must be called from the UI thread.
/// Side effects: Adds and removes a modal-looking root visual that consumes pointer input until closed.
/// </remarks>
internal static class CenteredDialogOverlay
{
    /// <summary>Shows centered modal content and completes when the close button or popup close event fires.</summary>
    public static Task ShowAsync(
        XamlRoot xamlRoot,
        string title,
        UIElement content,
        string closeButtonText,
        double dialogWidth,
        CancellationToken cancellationToken)
    {
        return WindowDialogCoordinator.TryShowOverlayAsync(
            xamlRoot,
            token => ShowCoreAsync(xamlRoot, title, content, closeButtonText, dialogWidth, token),
            cancellationToken);
    }

    private static async Task ShowCoreAsync(
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

        if (App.MainWindow?.Content is not Panel rootPanel)
        {
            throw new InvalidOperationException("The main window content root must be a Panel to host centered overlays.");
        }

        TaskCompletionSource<object?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Button closeButton = BuildCloseButton(closeButtonText);
        Grid overlay = BuildOverlay(xamlRoot, title, content, closeButton, dialogWidth);
        Window? window = App.MainWindow;
        int closeRequested = 0;
        Grid.SetRow(overlay, 0);
        if (rootPanel is Grid rootGrid)
        {
            Grid.SetRowSpan(overlay, Math.Max(1, rootGrid.RowDefinitions.Count));
        }
        Canvas.SetZIndex(overlay, 10_000);

        async void OnCloseButtonClick(object sender, RoutedEventArgs args)
        {
            if (Interlocked.Exchange(ref closeRequested, 1) != 0)
            {
                return;
            }

            closeButton.IsEnabled = false;
            try
            {
                await PlayCloseAnimationAsync(overlay).ConfigureAwait(true);
            }
            catch (Exception exception) when (
                !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                // Closing the modal must remain reliable even when a cosmetic animation fails.
            }
            finally
            {
                closed.TrySetResult(null);
            }
        }

        void CompleteForVisualTreeTeardown(object sender, RoutedEventArgs args)
        {
            closed.TrySetResult(null);
        }

        void CompleteForWindowClose(object sender, WindowEventArgs args)
        {
            closed.TrySetResult(null);
        }

        rootPanel.Children.Add(overlay);
        closeButton.Click += OnCloseButtonClick;
        rootPanel.Unloaded += CompleteForVisualTreeTeardown;
        if (window is not null)
        {
            window.Closed += CompleteForWindowClose;
        }

        try
        {
            using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
                () => closed.TrySetCanceled(cancellationToken));
            closeButton.Focus(FocusState.Programmatic);
            await closed.Task;
        }
        finally
        {
            closeButton.Click -= OnCloseButtonClick;
            rootPanel.Unloaded -= CompleteForVisualTreeTeardown;
            if (window is not null)
            {
                window.Closed -= CompleteForWindowClose;
            }

            rootPanel.Children.Remove(overlay);
        }
    }

    private static Grid BuildOverlay(XamlRoot xamlRoot, string title, UIElement content, Button closeButton, double dialogWidth)
    {
        (double Width, double Height) overlaySize = ResolveOverlaySize(xamlRoot);
        Grid overlay = new()
        {
            MinWidth = overlaySize.Width,
            MinHeight = overlaySize.Height,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 0, 0, 0)),
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
            RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 },
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
    }

    private static Task PlayCloseAnimationAsync(Grid overlay)
    {
        TaskCompletionSource<object?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (overlay.Children.FirstOrDefault() is not Border card
            || card.RenderTransform is not ScaleTransform scale)
        {
            completed.SetResult(null);
            return completed.Task;
        }

        Storyboard storyboard = new();
        storyboard.Children.Add(CreateDoubleAnimation(overlay, "Opacity", 0, 120));
        storyboard.Children.Add(CreateDoubleAnimation(scale, "ScaleX", 0.98, 120));
        storyboard.Children.Add(CreateDoubleAnimation(scale, "ScaleY", 0.98, 120));
        storyboard.Completed += (_, _) => completed.TrySetResult(null);
        storyboard.Begin();
        return completed.Task;
    }

    private static DoubleAnimation CreateDoubleAnimation(DependencyObject target, string property, double to, double milliseconds)
    {
        DoubleAnimation animation = new()
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
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
