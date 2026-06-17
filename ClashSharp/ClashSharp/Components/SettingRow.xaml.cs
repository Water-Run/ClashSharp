/*
 * Setting Row Component
 * Provides a reusable title, description, and action layout for settings pages
 *
 * @author: WaterRun
 * @file: Components/SettingRow.xaml.cs
 * @date: 2026-06-17
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>A reusable settings row with text content on the left and an action control on the right.</summary>
/// <remarks>
/// Invariants: Title and description default to empty strings.
/// Thread safety: Must be created and accessed from the UI thread only.
/// Side effects: None beyond normal dependency property updates.
/// </remarks>
public sealed partial class SettingRow : UserControl
{
    /// <summary>Dependency property backing <see cref="Title"/>.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(SettingRow),
        new PropertyMetadata(string.Empty));

    /// <summary>Dependency property backing <see cref="Description"/>.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(SettingRow),
        new PropertyMetadata(string.Empty));

    /// <summary>Dependency property backing <see cref="ActionContent"/>.</summary>
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent),
        typeof(object),
        typeof(SettingRow),
        new PropertyMetadata(null));

    /// <summary>Initializes the settings row component.</summary>
    public SettingRow()
    {
        InitializeComponent();
    }

    /// <summary>Gets or sets the primary row title.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Gets or sets the secondary row description.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Gets or sets the action control displayed at the right edge of the row.</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }
}
