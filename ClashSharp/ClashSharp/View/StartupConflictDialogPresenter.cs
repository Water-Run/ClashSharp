/*
 * Startup Conflict Dialog Presenter
 * Builds and shows reusable startup conflict dialogs for startup flow and settings-triggered checks
 *
 * @author: WaterRun
 * @file: View/StartupConflictDialogPresenter.cs
 * @date: 2026-06-24
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.View;

/// <summary>Builds and shows startup conflict dialogs shared by startup flow and settings actions.</summary>
/// <remarks>
/// Invariants: Dialog content is generated from an immutable conflict issue snapshot.
/// Thread safety: Must be called on the UI thread.
/// Side effects: Shows a modal XAML dialog and may execute repair callbacks selected by the user.
/// </remarks>
internal static class StartupConflictDialogPresenter
{
    /// <summary>Shows the startup conflict dialog for the supplied issue snapshot.</summary>
    /// <param name="xamlRoot">XAML root used to host the dialog. Must not be null.</param>
    /// <param name="issues">Detected conflict issues. Must not be null.</param>
    /// <returns>A task that completes after the dialog closes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="xamlRoot"/> or <paramref name="issues"/> is null.</exception>
    public static async Task ShowAsync(XamlRoot xamlRoot, IReadOnlyList<StartupConflictIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(issues);

        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("StartupConflict.Dialog.Title"),
            Content = BuildContent(issues, xamlRoot),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Close"),
            XamlRoot = xamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>Builds dialog content for either an empty result or a list of repairable issues.</summary>
    /// <param name="issues">Detected conflict issues. Must not be null.</param>
    /// <param name="xamlRoot">XAML root used to size the scroll region. Must not be null.</param>
    /// <returns>Dialog content element.</returns>
    private static UIElement BuildContent(IReadOnlyList<StartupConflictIssue> issues, XamlRoot xamlRoot)
    {
        if (issues.Count == 0)
        {
            return new TextBlock
            {
                Text = LocalizationService.Instance.GetString("StartupConflict.Dialog.Empty"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640,
            };
        }

        StackPanel panel = new()
        {
            Spacing = 8,
            MinWidth = 420,
            MaxWidth = 680,
        };
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Instance.GetString("StartupConflict.Dialog.Introduction"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (StartupConflictIssue issue in issues)
        {
            panel.Children.Add(BuildIssueRow(issue));
        }

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Min(420, Math.Max(240, xamlRoot.Size.Height - 220)),
            Padding = new Thickness(0, 0, 12, 0),
        };
    }

    /// <summary>Builds one repairable conflict issue row.</summary>
    /// <param name="issue">Conflict issue snapshot. Must not be null.</param>
    /// <returns>Issue row element.</returns>
    private static Grid BuildIssueRow(StartupConflictIssue issue)
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
            Text = LocalizationService.Instance.GetString("StartupConflict.Status.Ready"),
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
            repairButton.IsEnabled = false;
            progressRing.Visibility = Visibility.Visible;
            progressRing.IsActive = true;
            statusText.Text = LocalizationService.Instance.GetString("StartupConflict.Status.Fixing");

            StartupConflictRepairResult result = await issue.RepairAsync(default);
            progressRing.IsActive = false;
            progressRing.Visibility = Visibility.Collapsed;
            statusText.Text = result.Succeeded
                ? LocalizationService.Instance.GetString("StartupConflict.Status.Succeeded")
                : LocalizationService.Instance.GetString("StartupConflict.Status.Failed");
            ToolTipService.SetToolTip(statusText, result.Message);
        };
        Grid.SetRow(actionPanel, 1);
        row.Children.Add(actionPanel);

        return row;
    }
}
