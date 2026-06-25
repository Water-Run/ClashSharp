/*
 * Master Info Tile
 * Reusable information tile for the master control page
 *
 * @author: WaterRun
 * @file: Components/MasterInfoTile.xaml.cs
 * @date: 2026-06-25
 */

using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Reusable compact information tile with an optional switch action.</summary>
public sealed partial class MasterInfoTile : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsToggleVisibleProperty = DependencyProperty.Register(
        nameof(IsToggleVisible),
        typeof(bool),
        typeof(MasterInfoTile),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsToggleOnProperty = DependencyProperty.Register(
        nameof(IsToggleOn),
        typeof(bool),
        typeof(MasterInfoTile),
        new PropertyMetadata(false));

    public static readonly DependencyProperty ToggleCommandProperty = DependencyProperty.Register(
        nameof(ToggleCommand),
        typeof(ICommand),
        typeof(MasterInfoTile),
        new PropertyMetadata(null));

    private bool _isLoaded;

    public MasterInfoTile()
    {
        InitializeComponent();
        Loaded += (_, _) => _isLoaded = true;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool IsToggleVisible
    {
        get => (bool)GetValue(IsToggleVisibleProperty);
        set => SetValue(IsToggleVisibleProperty, value);
    }

    public bool IsToggleOn
    {
        get => (bool)GetValue(IsToggleOnProperty);
        set => SetValue(IsToggleOnProperty, value);
    }

    public ICommand? ToggleCommand
    {
        get => (ICommand?)GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    private void ToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded || ToggleCommand is not ICommand command || !command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
    }
}
