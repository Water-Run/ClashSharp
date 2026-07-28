using System;
using System.Collections.Generic;
using ClashSharp.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.Components;

/// <summary>Renders a pre-collected startup-health snapshot in a reusable content dialog.</summary>
/// <remarks>
/// Invariants: The dialog renders exactly the immutable check snapshot supplied by its presenter.
/// Thread safety: Must be created and shown on the UI thread.
/// Side effects: None until the dialog is shown by a caller.
/// </remarks>
public sealed partial class StartupGuideDialog : ContentDialog
{
    /// <summary>Initializes the startup guide from a pre-collected health snapshot.</summary>
    /// <param name="checks">Health rows collected before the visual component is created.</param>
    /// <param name="getString">Localization dependency for dialog-only display text.</param>
    public StartupGuideDialog(
        IReadOnlyList<StartupCheckItem> checks,
        Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(getString);
        InitializeComponent();
        Title = getString("Settings.StartupGuide.Title");
        CloseButtonText = getString("Command.Close");
        GuideDescriptionText.Text = getString("Settings.StartupGuide.Description");
        foreach (StartupCheckItem check in checks)
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
