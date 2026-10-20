using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Builds and shows startup conflict dialogs shared by startup flow and settings actions.</summary>
/// <remarks>
/// Invariants: Dialog content is generated from an immutable conflict issue snapshot.
/// Thread safety: Must be called on the UI thread.
/// Side effects: Shows a modal XAML dialog and may execute repair callbacks selected by the user.
/// </remarks>
internal static class StartupConflictDialogPresenter
{
    private const double DialogWidth = 560;

    /// <summary>Shows the startup conflict dialog for the supplied issue snapshot.</summary>
    /// <param name="xamlRoot">XAML root used to host the dialog. Must not be null.</param>
    /// <param name="issues">Detected conflict issues. Must not be null.</param>
    /// <param name="getString">Localization resolver used for every dialog label.</param>
    /// <param name="errorSink">Boundary sink for unexpected repair-command failures.</param>
    /// <param name="cancellationToken">Cancels pending repair work and closes the dialog lifetime.</param>
    /// <returns>A task that completes after the dialog closes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="xamlRoot"/> or <paramref name="issues"/> is null.</exception>
    public static async Task ShowAsync(
        XamlRoot xamlRoot,
        IReadOnlyList<StartupConflictIssue> issues,
        Func<string, string> getString,
        IApplicationErrorSink errorSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(getString);
        ArgumentNullException.ThrowIfNull(errorSink);
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource dialogLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            List<Action> refreshText = [];
            await CenteredDialogOverlay.ShowAsync(
                xamlRoot,
                getString("StartupConflict.Dialog.Title"),
                BuildContent(issues, xamlRoot, getString, errorSink, refreshText, dialogLifetime.Token),
                getString("Command.Close"),
                DialogWidth,
                dialogLifetime.Token,
                dialog => dialog.Localize(() =>
                {
                    dialog.Title = getString("StartupConflict.Dialog.Title");
                    dialog.CloseButtonText = getString("Command.Close");
                    foreach (Action refresh in refreshText) { refresh(); }
                }));
        }
        catch (OperationCanceledException exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
        }
        finally
        {
            dialogLifetime.Cancel();
        }
    }

    /// <summary>Builds dialog content for either an empty result or a list of repairable issues.</summary>
    /// <param name="issues">Detected conflict issues. Must not be null.</param>
    /// <param name="xamlRoot">XAML root used to size the scroll region. Must not be null.</param>
    /// <param name="getString">Localization resolver used for content labels.</param>
    /// <param name="errorSink">Boundary sink supplied to each repair command.</param>
    /// <param name="cancellationToken">Cancels repair commands owned by this content lifetime.</param>
    /// <param name="refreshText">Display callbacks retained only for this dialog.</param>
    /// <returns>Dialog content element.</returns>
    private static UIElement BuildContent(
        IReadOnlyList<StartupConflictIssue> issues,
        XamlRoot xamlRoot,
        Func<string, string> getString,
        IApplicationErrorSink errorSink,
        List<Action> refreshText,
        CancellationToken cancellationToken)
    {
        if (issues.Count == 0)
        {
            TextBlock empty = new()
            {
                Text = getString("StartupConflict.Dialog.Empty"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            };
            refreshText.Add(() => empty.Text = getString("StartupConflict.Dialog.Empty"));
            return empty;
        }

        StackPanel panel = new()
        {
            Spacing = 8,
            MinWidth = 420,
            MaxWidth = 500,
        };
        TextBlock introduction = new()
        {
            Text = getString("StartupConflict.Dialog.Introduction"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(introduction);
        refreshText.Add(() => introduction.Text = getString("StartupConflict.Dialog.Introduction"));

        foreach (StartupConflictIssue issue in issues)
        {
            StartupConflictDisplay display = new(issue, getString);
            refreshText.Add(display.RefreshLocalization);
            panel.Children.Add(BuildIssueRow(display, errorSink, cancellationToken));
        }

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Min(420, Math.Max(180, xamlRoot.Size.Height - 220)),
            Padding = new Thickness(0, 0, 12, 0),
        };
    }

    /// <summary>Builds one repairable conflict issue row.</summary>
    /// <param name="display">Captured issue and its current presentation state.</param>
    /// <param name="errorSink">Boundary sink for unexpected repair failures.</param>
    /// <param name="cancellationToken">Cancels the row's repair command.</param>
    /// <returns>Issue row element.</returns>
    private static Grid BuildIssueRow(
        StartupConflictDisplay display,
        IApplicationErrorSink errorSink,
        CancellationToken cancellationToken)
    {
        StartupConflictIssue issue = display.Issue;
        Grid row = new()
        {
            Style = (Style)Application.Current.Resources["ClashSectionRowStyle"],
            Padding = new Thickness(0, 12, 0, 12),
            RowSpacing = 8,
        };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel textPanel = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 3,
        };
        TextBlock title = new()
        {
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        BindText(title, display, nameof(StartupConflictDisplay.Title));
        textPanel.Children.Add(title);
        TextBlock description = new()
        {
            Style = (Style)Application.Current.Resources["ClashSecondaryCaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        BindText(description, display, nameof(StartupConflictDisplay.Description));
        textPanel.Children.Add(description);

        TextBlock statusText = new()
        {
            Style = (Style)Application.Current.Resources["ClashSecondaryCaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        BindText(statusText, display, nameof(StartupConflictDisplay.StatusText));
        statusText.SetBinding(ToolTipService.ToolTipProperty, new Binding
        { Source = display, Path = new PropertyPath(nameof(StartupConflictDisplay.StatusText)), Mode = BindingMode.OneWay });
        textPanel.Children.Add(statusText);
        row.Children.Add(textPanel);

        if (!issue.HasRepairAction)
        {
            return row;
        }

        StackPanel actionPanel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ProgressRing progressRing = new()
        {
            Width = 18,
            Height = 18,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionPanel.Children.Add(progressRing);

        HyperlinkButton repairButton = new()
        {
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        repairButton.SetBinding(ContentControl.ContentProperty, new Binding
        { Source = display, Path = new PropertyPath(nameof(StartupConflictDisplay.RepairText)), Mode = BindingMode.OneWay });
        actionPanel.Children.Add(repairButton);
        repairButton.Click += async (_, _) =>
        {
            if (!display.BeginRepair())
            {
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progressRing.Visibility = Visibility.Visible;
                progressRing.IsActive = true;

                StartupConflictRepairResult result = await issue.RepairAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                display.CompleteRepair(result);
            }
            catch (OperationCanceledException exception) when (
                ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
            {
            }
            catch (Exception exception) when (
                !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                display.CompleteRepair(new(false, string.Empty) { MessageSource = new("StartupConflict.Status.Failed") });
                await ReportUnexpectedAsync(errorSink, exception, cancellationToken);
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    display.EndRepair();
                    progressRing.IsActive = false;
                    progressRing.Visibility = Visibility.Collapsed;
                    repairButton.IsEnabled = display.CanRepair;
                }
            }
        };
        Grid.SetRow(actionPanel, 1);
        row.Children.Add(actionPanel);

        return row;
    }

    private static void BindText(TextBlock text, StartupConflictDisplay display, string property) =>
        text.SetBinding(TextBlock.TextProperty, new Binding
        { Source = display, Path = new PropertyPath(property), Mode = BindingMode.OneWay });

    private static async Task ReportUnexpectedAsync(
        IApplicationErrorSink errorSink,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await errorSink.ReportAsync(
                new ApplicationError("startup-conflict-repair", exception),
                cancellationToken);
        }
        catch (Exception sinkException) when (
            !ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
            // This UI event boundary has no independent secondary diagnostic channel.
        }
    }
}
