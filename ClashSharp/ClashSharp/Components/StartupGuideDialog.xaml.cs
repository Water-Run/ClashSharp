/*
 * Startup Guide Dialog
 * Reserves the reusable startup guide dialog shell for quick-start content
 *
 * @author: WaterRun
 * @file: Components/StartupGuideDialog.xaml.cs
 * @date: 2026-06-24
 */

using ClashSharp.Service;
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
    /// <summary>Initializes the startup guide dialog shell with localized text.</summary>
    public StartupGuideDialog()
    {
        InitializeComponent();
        Title = LocalizationService.Instance.GetString("Settings.StartupGuide.Title");
        CloseButtonText = LocalizationService.Instance.GetString("Command.Close");
        GuideDescriptionText.Text = LocalizationService.Instance.GetString("Settings.StartupGuide.Description");
        LoadChecks();
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
