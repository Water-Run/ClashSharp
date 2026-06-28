/*
 * Startup Guide Dialog
 * Reserves the reusable startup guide dialog shell for quick-start content
 *
 * @author: WaterRun
 * @file: Components/StartupGuideDialog.xaml.cs
 * @date: 2026-06-24
 */

using System;
using System.Threading.Tasks;
using ClashSharp.Service;
using ClashSharp.View;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.Components;

/// <summary>Reusable dialog shell reserved for startup guide content.</summary>
/// <remarks>
/// Invariants: Dialog text is resolved from localization resources during construction.
/// Thread safety: Must be created and shown on the UI thread.
/// Side effects: None until the dialog is shown by a caller.
/// </remarks>
public sealed partial class StartupGuideDialog : ContentDialog
{
    private const double DialogWidth = 520;

    /// <summary>Initializes the startup guide dialog shell with localized text.</summary>
    public StartupGuideDialog()
    {
        InitializeComponent();
        Title = LocalizationService.Instance.GetString("Settings.StartupGuide.Title");
        CloseButtonText = LocalizationService.Instance.GetString("Command.Close");
        GuideDescriptionText.Text = LocalizationService.Instance.GetString("Settings.StartupGuide.Description");
        LoadChecks();
    }

    /// <summary>Shows startup guidance in the app-owned centered overlay.</summary>
    /// <param name="xamlRoot">Window-level XAML root that hosts the dialog. Not null.</param>
    /// <returns>A task that completes when the user closes the overlay.</returns>
    public Task ShowCenteredAsync(XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);

        return CenteredDialogOverlay.ShowAsync(
            xamlRoot,
            LocalizationService.Instance.GetString("Settings.StartupGuide.Title"),
            BuildGuideContent(),
            LocalizationService.Instance.GetString("Command.Close"),
            DialogWidth);
    }

    private static StackPanel BuildGuideContent()
    {
        StackPanel panel = new()
        {
            MinWidth = 360,
            MaxWidth = 480,
            Spacing = 12,
        };
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Instance.GetString("Settings.StartupGuide.Description"),
            TextWrapping = TextWrapping.WrapWholeWords,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });

        StackPanel checksPanel = new()
        {
            Spacing = 8,
        };
        foreach (StartupCheckItem check in StartupCheckService.Instance.GetChecks())
        {
            checksPanel.Children.Add(BuildCheckRow(check));
        }
        panel.Children.Add(new ScrollViewer
        {
            Content = checksPanel,
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        return panel;
    }

    private void LoadChecks()
    {
        ChecksPanel.Children.Clear();
        foreach (StartupCheckItem check in StartupCheckService.Instance.GetChecks())
        {
            ChecksPanel.Children.Add(BuildCheckRow(check));
        }
    }

    private static Border BuildCheckRow(StartupCheckItem check)
    {
        Border border = new()
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        Grid row = new()
        {
            ColumnSpacing = 10,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FontIcon icon = new()
        {
            Glyph = check.IsHealthy ? "\uE73E" : "\uE7BA",
            FontSize = 16,
            Width = 22,
            Foreground = new SolidColorBrush(check.IsHealthy ? Colors.SeaGreen : Colors.DarkOrange),
            VerticalAlignment = VerticalAlignment.Top,
        };
        row.Children.Add(icon);

        StackPanel textPanel = new()
        {
            Spacing = 2,
        };
        Grid.SetColumn(textPanel, 1);
        textPanel.Children.Add(new TextBlock
        {
            Text = check.Title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = check.Description,
            TextWrapping = TextWrapping.WrapWholeWords,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        row.Children.Add(textPanel);

        border.Child = row;
        return border;
    }
}
