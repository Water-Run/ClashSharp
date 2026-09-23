using System;
using System.Linq;
using System.Windows.Input;
using ClashSharp.ApplicationModel.Presentation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClashSharp.Components;

/// <summary>Reusable compact information tile with an optional switch action.</summary>
public sealed partial class MasterInfoTile : UserControl
{
    private const double DragSuppressThreshold = 8;

    private Point? _pressedPoint;
    private bool _suppressNextTap;

    private ICommand? _observedCommand;

    private bool _isLoaded;

    /// <summary>Identifies the localized hint for the separate drag handle.</summary>
    public static readonly DependencyProperty ReorderHintProperty = DependencyProperty.Register(
        nameof(ReorderHint), typeof(string), typeof(MasterInfoTile), new PropertyMetadata(string.Empty));

    /// <summary>Gets or sets the drag handle's accessible description.</summary>
    public string ReorderHint
    {
        get => (string)GetValue(ReorderHintProperty);
        set => SetValue(ReorderHintProperty, value);
    }

    /// <summary>Identifies the recent rate samples shown as a compact trend.</summary>
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(double[]), typeof(MasterInfoTile),
        new PropertyMetadata(null, OnHistoryChanged));

    /// <summary>Gets or sets up to sixty recent rate samples.</summary>
    public double[]? History
    {
        get => (double[]?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    private static void OnHistoryChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        MasterInfoTile tile = (MasterInfoTile)sender;
        if (tile.HistoryCanvas is null) { return; }
        double[] samples = tile.History ?? [];
        tile.HistoryCanvas.Visibility = samples.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        double peak = Math.Max(1, samples.DefaultIfEmpty(0).Max());
        PointCollection points = new();
        for (int index = 0; index < samples.Length; index++)
        {
            points.Add(new Point(1 + index * 70d / Math.Max(1, samples.Length - 1), 34 - Math.Clamp(samples[index] / peak, 0, 1) * 32));
        }
        tile.HistoryLine.Points = points;
    }

    /// <summary>Identifies the <see cref="Title"/> dependency property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(MasterInfoTile),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Description"/> dependency property.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
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
        new PropertyMetadata(null, OnTileCommandChanged));

    /// <summary>Initializes an information tile and its pointer-aware visual states.</summary>
    public MasterInfoTile()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Native Button supplies keyboard, focus and UIA Invoke behavior. Observe handled
        // pointer events as well, because Button may capture them before the parent GridView.
        TileButton.AddHandler(PointerPressedEvent, new PointerEventHandler(TileRoot_PointerPressed), true);
        TileButton.AddHandler(PointerMovedEvent, new PointerEventHandler(TileRoot_PointerMoved), true);
        TileButton.AddHandler(PointerReleasedEvent, new PointerEventHandler(TileRoot_PointerReleased), true);
        TileButton.AddHandler(PointerCanceledEvent, new PointerEventHandler(TileRoot_PointerCanceled), true);
    }

    /// <summary>Gets or sets the tile's primary label.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Gets or sets the localized explanation exposed by the action tooltip.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
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

    private void TileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressNextTap)
        {
            _suppressNextTap = false;
            return;
        }

        if (TileCommand is not ICommand command)
        {
            StackPanel content = new() { Spacing = 8, MaxWidth = 420 };
            foreach (string text in new[] { Title, Value, Detail, Description })
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                }
            }
            Flyout flyout = new() { Content = new ScrollViewer { Content = content, MaxHeight = 360 } };
            flyout.ShowAt(TileButton);
            return;
        }

        if (!command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
    }

    private void TileRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _ = VisualStateManager.GoToState(this, "PointerOver", true);
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
        // A drag may suppress native Click entirely. Clear its residue after this input
        // message so the next keyboard/UIA activation is never mistaken for the drag.
        DispatcherQueue.TryEnqueue(() => _suppressNextTap = false);
    }

    private void TileRoot_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _pressedPoint = null;
        _suppressNextTap = false;
        _ = VisualStateManager.GoToState(this, "Normal", true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ObserveCommand();
        UpdateVisualState(useTransitions: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        ObserveCommand();
        _pressedPoint = null;
        _suppressNextTap = false;
    }

    private static void OnTileCommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((MasterInfoTile)dependencyObject).ObserveCommand();
    }

    private void ObserveCommand()
    {
        if (_observedCommand is not null)
        {
            _observedCommand.CanExecuteChanged -= OnCanExecuteChanged;
        }

        _observedCommand = _isLoaded ? TileCommand : null;
        if (_observedCommand is not null)
        {
            _observedCommand.CanExecuteChanged += OnCanExecuteChanged;
        }

        UpdateCommandState();
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e)
    {
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        if (TileButton is null)
        {
            return;
        }

        bool available = _isLoaded && (TileCommand is null || TileCommand.CanExecute(null));
        bool running = _isLoaded && TileCommand is IAsyncCommandState { IsRunning: true };
        TileButton.Visibility = Visibility.Visible;
        TileButton.IsEnabled = available;
        ExecutionProgress.IsActive = running;
        ExecutionProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ProtectedCursor = available ? InputSystemCursor.Create(InputSystemCursorShape.Hand) : null;
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
