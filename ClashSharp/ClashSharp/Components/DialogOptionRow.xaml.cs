/*
 * Dialog Option Row
 * Reusable dialog row for option lists with icon, title, category, description, and checked state
 *
 * @author: WaterRun
 * @file: Components/DialogOptionRow.xaml.cs
 * @date: 2026-06-26
 */

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Reusable option row for dialog choice lists.</summary>
public sealed partial class DialogOptionRow : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(DialogOptionRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MetadataProperty = DependencyProperty.Register(
        nameof(Metadata),
        typeof(string),
        typeof(DialogOptionRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(DialogOptionRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(DialogOptionRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked),
        typeof(bool?),
        typeof(DialogOptionRow),
        new PropertyMetadata(false));

    public DialogOptionRow()
    {
        InitializeComponent();
    }

    public event EventHandler? SelectionInvoked;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Metadata
    {
        get => (string)GetValue(MetadataProperty);
        set => SetValue(MetadataProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool? IsChecked
    {
        get => (bool?)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    private void OptionButton_Click(object sender, RoutedEventArgs e)
    {
        SelectionInvoked?.Invoke(this, EventArgs.Empty);
    }
}
