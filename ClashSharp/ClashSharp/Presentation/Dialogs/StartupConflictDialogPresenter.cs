using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
            await CenteredDialogOverlay.ShowAsync(
                xamlRoot,
                getString("StartupConflict.Dialog.Title"),
                BuildContent(issues, xamlRoot, getString, errorSink, dialogLifetime.Token),
                getString("Command.Close"),
                DialogWidth,
                dialogLifetime.Token);
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
    /// <returns>Dialog content element.</returns>
    private static UIElement BuildContent(
        IReadOnlyList<StartupConflictIssue> issues,
        XamlRoot xamlRoot,
        Func<string, string> getString,
        IApplicationErrorSink errorSink,
        CancellationToken cancellationToken)
    {
        if (issues.Count == 0)
        {
            return new TextBlock
            {
                Text = getString("StartupConflict.Dialog.Empty"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            };
        }

        StackPanel panel = new()
        {
            Spacing = 8,
            MinWidth = 420,
            MaxWidth = 500,
        };
        panel.Children.Add(new TextBlock
        {
            Text = getString("StartupConflict.Dialog.Introduction"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (StartupConflictIssue issue in issues)
        {
            panel.Children.Add(BuildIssueRow(issue, getString, errorSink, cancellationToken));
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
    /// <param name="issue">Conflict issue snapshot. Must not be null.</param>
    /// <returns>Issue row element.</returns>
    private static Grid BuildIssueRow(
        StartupConflictIssue issue,
        Func<string, string> getString,
        IApplicationErrorSink errorSink,
        CancellationToken cancellationToken)
    {
        Grid row = new()
        {
            Style = (Style)Application.Current.Resources["ClashCardGridStyle"],
            RowSpacing = 8,
        };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel textPanel = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 3,
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = issue.Title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = issue.Description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        TextBlock statusText = new()
        {
            Text = getString("StartupConflict.Status.Ready"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        textPanel.Children.Add(statusText);
        row.Children.Add(textPanel);

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
            Content = issue.RepairText,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionPanel.Children.Add(repairButton);
        repairButton.Click += async (_, _) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                repairButton.IsEnabled = false;
                progressRing.Visibility = Visibility.Visible;
                progressRing.IsActive = true;
                statusText.Text = getString("StartupConflict.Status.Fixing");

                StartupConflictRepairResult result = await issue.RepairAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                statusText.Text = result.Succeeded
                    ? getString("StartupConflict.Status.Succeeded")
                    : getString("StartupConflict.Status.Failed");
                ToolTipService.SetToolTip(statusText, result.Message);
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

                statusText.Text = getString("StartupConflict.Status.Failed");
                ToolTipService.SetToolTip(statusText, statusText.Text);
                await ReportUnexpectedAsync(errorSink, exception, cancellationToken);
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    progressRing.IsActive = false;
                    progressRing.Visibility = Visibility.Collapsed;
                }
            }
        };
        Grid.SetRow(actionPanel, 1);
        row.Children.Add(actionPanel);

        return row;
    }

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
