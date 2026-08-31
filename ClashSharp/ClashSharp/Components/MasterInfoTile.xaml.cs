using System.Windows.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace ClashSharp.Components;

/// <summary>Reusable compact information tile with an optional switch action.</summary>
public sealed partial class MasterInfoTile : UserControl
{
    private const double DragSuppressThreshold = 8;

    private Point? _pressedPoint;
    private bool _suppressNextTap;

    /// <summary>Identifies the <see cref="Title"/> dependency property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Value"/> dependency property.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Detail"/> dependency property.</summary>
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Glyph"/> dependency property.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="IsToggleVisible"/> dependency property.</summary>
    public static readonly DependencyProperty IsToggleVisibleProperty = DependencyProperty.Register(
        nameof(IsToggleVisible),
        typeof(bool),
        typeof(MasterInfoTile),
        new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="IsToggleOn"/> dependency property.</summary>
    public static readonly DependencyProperty IsToggleOnProperty = DependencyProperty.Register(
        nameof(IsToggleOn),
        typeof(bool),
        typeof(MasterInfoTile),
        new PropertyMetadata(false, OnIsToggleOnChanged));

    /// <summary>Identifies the <see cref="TileCommand"/> dependency property.</summary>
    public static readonly DependencyProperty TileCommandProperty = DependencyProperty.Register(
        nameof(TileCommand),
        typeof(ICommand),
        typeof(MasterInfoTile),
        new PropertyMetadata(null));

    /// <summary>Initializes an information tile and its pointer-aware visual states.</summary>
    public MasterInfoTile()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ProtectedCursor = TileCommand is null ? null : InputSystemCursor.Create(InputSystemCursorShape.Hand);
            UpdateVisualState(useTransitions: false);
        };
    }

    /// <summary>Gets or sets the tile's primary label.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Gets or sets the tile's emphasized current value.</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Gets or sets supporting detail shown with the current value.</summary>
    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Gets or sets the glyph that identifies the information category.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Gets or sets whether the tile displays its toggle indicator.</summary>
    public bool IsToggleVisible
    {
        get => (bool)GetValue(IsToggleVisibleProperty);
        set => SetValue(IsToggleVisibleProperty, value);
    }

    /// <summary>Gets or sets the effective state represented by the toggle indicator.</summary>
    public bool IsToggleOn
    {
        get => (bool)GetValue(IsToggleOnProperty);
        set => SetValue(IsToggleOnProperty, value);
    }

    /// <summary>Gets or sets the command invoked when the user activates the tile.</summary>
    public ICommand? TileCommand
    {
        get => (ICommand?)GetValue(TileCommandProperty);
        set => SetValue(TileCommandProperty, value);
    }

    private void TileRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_suppressNextTap)
        {
            _suppressNextTap = false;
            return;
        }

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
        _pressedPoint = e.GetCurrentPoint(TileRoot).Position;
        _suppressNextTap = false;
        if (TileCommand is not null)
        {
            _ = VisualStateManager.GoToState(this, "Pressed", true);
        }
    }

    private void TileRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedPoint is not Point pressedPoint)
        {
            return;
        }

        Point currentPoint = e.GetCurrentPoint(TileRoot).Position;
        double deltaX = currentPoint.X - pressedPoint.X;
        double deltaY = currentPoint.Y - pressedPoint.Y;
        if ((deltaX * deltaX) + (deltaY * deltaY) >= DragSuppressThreshold * DragSuppressThreshold)
        {
            _suppressNextTap = true;
            _ = VisualStateManager.GoToState(this, "Normal", true);
        }
    }

    private void TileRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressedPoint = null;
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
