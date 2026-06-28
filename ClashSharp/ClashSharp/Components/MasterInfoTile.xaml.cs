/*
 * Master Info Tile
 * Reusable information tile for the master control page
 *
 * @author: WaterRun
 * @file: Components/MasterInfoTile.xaml.cs
 * @date: 2026-06-25
 */

using System.Windows.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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
        new PropertyMetadata(false, OnIsToggleOnChanged));

    public static readonly DependencyProperty TileCommandProperty = DependencyProperty.Register(
        nameof(TileCommand),
        typeof(ICommand),
        typeof(MasterInfoTile),
        new PropertyMetadata(null));

    public MasterInfoTile()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ProtectedCursor = TileCommand is null ? null : InputSystemCursor.Create(InputSystemCursorShape.Hand);
            UpdateVisualState(useTransitions: false);
        };
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

    public ICommand? TileCommand
    {
        get => (ICommand?)GetValue(TileCommandProperty);
        set => SetValue(TileCommandProperty, value);
    }

    private void TileRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (TileCommand is not ICommand command || !command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
    }

    private void TileRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (TileCommand is not null)
        {
            _ = VisualStateManager.GoToState(this, "PointerOver", true);
        }
    }

    private void TileRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _ = VisualStateManager.GoToState(this, "Normal", true);
    }

    private void TileRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TileCommand is not null)
        {
            _ = VisualStateManager.GoToState(this, "Pressed", true);
        }
    }

    private void TileRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _ = VisualStateManager.GoToState(this, "PointerOver", true);
    }

    private static void OnIsToggleOnChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is MasterInfoTile tile)
        {
            tile.UpdateVisualState(useTransitions: true);
        }
    }

    private void UpdateVisualState(bool useTransitions)
    {
        _ = VisualStateManager.GoToState(this, IsToggleOn ? "SwitchOn" : "SwitchOff", useTransitions);
    }
}
