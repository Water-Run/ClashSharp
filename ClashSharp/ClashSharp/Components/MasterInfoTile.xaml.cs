using System;
using System.Linq;
using System.Windows.Input;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace ClashSharp.Components;

/// <summary>Reusable compact information tile with an optional state action.</summary>
public sealed partial class MasterInfoTile : UserControl
{
    private ICommand? _observedCommand;

    private bool _isLoaded;
    private bool _isPressActive;
    private bool _isInReorderContext;
    private bool _isSelectedForEditing;
    private Storyboard? _tiltAnimation;
    private Storyboard? _contextAnimation;
    private Storyboard? _reflowAnimation;

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

    /// <summary>Identifies the optional command offered from the detail flyout.</summary>
    public static readonly DependencyProperty NavigationCommandProperty = DependencyProperty.Register(
        nameof(NavigationCommand), typeof(ICommand), typeof(MasterInfoTile), new PropertyMetadata(null));

    /// <summary>Identifies the localized destination label for the detail link.</summary>
    public static readonly DependencyProperty NavigationTextProperty = DependencyProperty.Register(
        nameof(NavigationText), typeof(string), typeof(MasterInfoTile), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the localized guidance for native tile reordering.</summary>
    public static readonly DependencyProperty ReorderHintProperty = DependencyProperty.Register(
        nameof(ReorderHint), typeof(string), typeof(MasterInfoTile), new PropertyMetadata(string.Empty));

    /// <summary>Gets or sets the guidance shown with the tile's tooltip.</summary>
    public string ReorderHint
    {
        get => (string)GetValue(ReorderHintProperty);
        set => SetValue(ReorderHintProperty, value);
    }

    /// <summary>Initializes an information tile and its pointer-aware visual states.</summary>
    public MasterInfoTile()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // The containing GridViewItem is the sole semantic input owner. A child Button
        // captures the pointer before GridView can recognize its standard reorder gesture.
        AddHandler(PointerPressedEvent, new PointerEventHandler(TileRoot_PointerPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(TileRoot_PointerMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(TileRoot_PointerReleased), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(TileRoot_PointerCanceled), true);
        SizeChanged += (_, _) =>
        {
            TileScaleTransform.CenterX = ActualWidth / 2;
            TileScaleTransform.CenterY = ActualHeight / 2;
        };
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

    /// <summary>Gets or sets whether the tile represents a state that its selected appearance reports.</summary>
    public bool IsToggleVisible
    {
        get => (bool)GetValue(IsToggleVisibleProperty);
        set => SetValue(IsToggleVisibleProperty, value);
    }

    /// <summary>Gets or sets the effective state shown by the tile's selected appearance.</summary>
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

    /// <summary>Gets or sets the optional command for opening a related shell page.</summary>
    public ICommand? NavigationCommand
    {
        get => (ICommand?)GetValue(NavigationCommandProperty);
        set => SetValue(NavigationCommandProperty, value);
    }

    /// <summary>Gets or sets the related page's localized link text.</summary>
    public string NavigationText
    {
        get => (string)GetValue(NavigationTextProperty);
        set => SetValue(NavigationTextProperty, value);
    }

    /// <summary>Combines the current tile text without empty or repeated explanations.</summary>
    /// <param name="value">Current primary value.</param>
    /// <param name="detail">Current secondary value.</param>
    /// <param name="description">Explanation of the tile's action or data.</param>
    /// <param name="reorderHint">Localized press-and-hold and arrangement guidance.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WinUI x:Bind calls this function through the control instance.")]
    public string GetTooltipText(string value, string detail, string description, string reorderHint) =>
        string.Join(Environment.NewLine, new[] { value, detail, description, reorderHint }
            .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.Ordinal));

    /// <summary>Builds the live detail paragraphs without empty or repeated values.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WinUI x:Bind calls this function through the control instance.")]
    public string GetDetailText(string title, string value, string detail, string description) =>
        string.Join(Environment.NewLine + Environment.NewLine, new[] { title, value, detail, description }
            .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.Ordinal));

    /// <summary>Shows the navigation link only while this tile has a labelled destination.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WinUI x:Bind calls this function through the control instance.")]
    public Visibility GetNavigationVisibility(ICommand? navigation, string text) =>
        navigation is not null && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Invokes the action or opens details after the containing native item accepts a click.</summary>
    internal void Activate()
    {
        if (!_isLoaded) { return; }

        if (TileCommand is not ICommand command)
        {
            if (XamlRoot.Content is not FrameworkElement root) { return; }
            Style presentation = new() { TargetType = typeof(FlyoutPresenter) };
            presentation.Setters.Add(new Setter(RequestedThemeProperty, root.RequestedTheme));
            presentation.Setters.Add(new Setter(LanguageProperty, root.Language));
            presentation.Setters.Add(new Setter(FlowDirectionProperty, root.FlowDirection));
            TileDetailsFlyout.FlyoutPresenterStyle = presentation;
            TileDetailsScroller.MaxHeight = Math.Max(1, Math.Min(360, XamlRoot.Size.Height - 120));
            TileDetailsFlyout.ShowAt(this);
            return;
        }

        if (!command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
    }

    private void TileDetailsFlyout_Opened(object? sender, object e)
    {
        if (!_isLoaded || XamlRoot.Content is not FrameworkElement root) { return; }
        // Follow the root's requested-theme dependency property; ActualTheme is an observed result.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(TileDetailsScroller);
            parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not FlyoutPresenter presenter) { continue; }
            presenter.SetBinding(RequestedThemeProperty, new Binding { Source = root, Path = new PropertyPath(nameof(RequestedTheme)), Mode = BindingMode.OneWay });
            presenter.SetBinding(LanguageProperty, new Binding { Source = root, Path = new PropertyPath(nameof(Language)), Mode = BindingMode.OneWay });
            presenter.SetBinding(FlowDirectionProperty, new Binding { Source = root, Path = new PropertyPath(nameof(FlowDirection)), Mode = BindingMode.OneWay });
            break;
        }
    }

    private void TileDetailsNavigation_Click(object sender, RoutedEventArgs e)
    {
        TileDetailsFlyout.Hide();
        // Read the current destination after dismissal; the tile may have been rebound while open.
        if (_isLoaded && NavigationCommand is ICommand navigation && navigation.CanExecute(null))
        {
            navigation.Execute(null);
        }
    }

    private void TileRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _ = VisualStateManager.GoToState(this, "PointerOver", true);
    }

    private void TileRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPressActive) { _ = VisualStateManager.GoToState(this, "Normal", true); }
    }

    private void TileRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginPressFeedback(e.GetCurrentPoint(this).Position);
    }

    /// <summary>Starts position-sensitive feedback after the grid has taken input ownership.</summary>
    internal void BeginPressFeedback(Point point)
    {
        _isPressActive = true;
        UpdatePressFeedback(point);
        _ = VisualStateManager.GoToState(this, "Pressed", true);
    }

    private void TileRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdatePressFeedback(e.GetCurrentPoint(this).Position);
    }

    private void TileRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ResetPointerFeedback();
        _ = VisualStateManager.GoToState(this, "PointerOver", true);
    }

    private void TileRoot_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        ResetPointerFeedback();
    }

    /// <summary>Clears press feedback when the containing grid ends or transfers the pointer.</summary>
    internal void ResetPointerFeedback()
    {
        _isPressActive = false;
        AnimateTilt(default, MasterTileMotionPolicy.ReleaseMilliseconds);
        _ = VisualStateManager.GoToState(this, "Normal", true);
    }

    /// <summary>Updates depression and tilt from a point in the unprojected tile's coordinates.</summary>
    internal void UpdatePressFeedback(Point point)
    {
        if (!_isPressActive || _isInReorderContext) { return; }
        MasterTileMotionPolicy.PressPose pose = MasterTileMotionPolicy.GetPressPose(point.X, point.Y, ActualWidth, ActualHeight);
        AnimateTilt(pose, MasterTileMotionPolicy.PressMilliseconds);
    }

    /// <summary>Gives surrounding tiles the compact editing appearance without changing layout geometry.</summary>
    internal void SetReorderContext(bool active, bool selected = false)
    {
        if (_isInReorderContext == active && _isSelectedForEditing == selected) { return; }
        _isInReorderContext = active;
        _isSelectedForEditing = selected;
        ResetPointerFeedback();
        double x = TileScaleTransform.ScaleX;
        double y = TileScaleTransform.ScaleY;
        _contextAnimation?.Stop();
        TileScaleTransform.ScaleX = x;
        TileScaleTransform.ScaleY = y;
        double scale = active
            ? selected ? MasterTileMotionPolicy.SelectedScale : MasterTileMotionPolicy.SurroundingScale : 1;
        Storyboard animation = new();
        AddMotion(animation, TileScaleTransform, nameof(ScaleTransform.ScaleX), scale, MasterTileMotionPolicy.ReleaseMilliseconds);
        AddMotion(animation, TileScaleTransform, nameof(ScaleTransform.ScaleY), scale, MasterTileMotionPolicy.ReleaseMilliseconds);
        _contextAnimation = animation;
        animation.Begin();
    }

    private void AnimateTilt(MasterTileMotionPolicy.PressPose pose, int milliseconds)
    {
        double x = TileTiltProjection.RotationX;
        double y = TileTiltProjection.RotationY;
        double depth = TileTiltProjection.GlobalOffsetZ;
        if (_tiltAnimation is null && Math.Abs(x - pose.RotationX) < 0.001 && Math.Abs(y - pose.RotationY) < 0.001
            && Math.Abs(depth - pose.Depth) < 0.001) { return; }
        _tiltAnimation?.Stop();
        TileTiltProjection.RotationX = x;
        TileTiltProjection.RotationY = y;
        TileTiltProjection.GlobalOffsetZ = depth;
        Storyboard animation = new();
        AddMotion(animation, TileTiltProjection, nameof(PlaneProjection.RotationX), pose.RotationX, milliseconds);
        AddMotion(animation, TileTiltProjection, nameof(PlaneProjection.RotationY), pose.RotationY, milliseconds);
        AddMotion(animation, TileTiltProjection, nameof(PlaneProjection.GlobalOffsetZ), pose.Depth, milliseconds);
        _tiltAnimation = animation;
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_tiltAnimation, animation)) { return; }
            _tiltAnimation = null;
            animation.Stop();
            TileTiltProjection.RotationX = pose.RotationX;
            TileTiltProjection.RotationY = pose.RotationY;
            TileTiltProjection.GlobalOffsetZ = pose.Depth;
        };
        animation.Begin();
    }

    private static void AddMotion(Storyboard owner, DependencyObject target, string property, double value, int milliseconds)
    {
        DoubleAnimation animation = new()
        {
            To = value,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        owner.Children.Add(animation);
    }

    /// <summary>Gets the current visual displacement while an adjacent tile is making room.</summary>
    internal Point ReflowOffset => new(TileReflowTransform.X, TileReflowTransform.Y);

    /// <summary>Moves from a captured rendered position into the newly arranged slot.</summary>
    internal void ReflowFrom(double x, double y)
    {
        _reflowAnimation?.Stop();
        _reflowAnimation = null;
        TileReflowTransform.X = x;
        TileReflowTransform.Y = y;
        if (Math.Abs(x) < 0.1 && Math.Abs(y) < 0.1) { return; }
        Storyboard animation = new();
        AddMotion(animation, TileReflowTransform, nameof(TranslateTransform.X), 0, MasterTileMotionPolicy.ReleaseMilliseconds);
        AddMotion(animation, TileReflowTransform, nameof(TranslateTransform.Y), 0, MasterTileMotionPolicy.ReleaseMilliseconds);
        _reflowAnimation = animation;
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_reflowAnimation, animation)) { return; }
            _reflowAnimation = null;
            animation.Stop();
            TileReflowTransform.X = TileReflowTransform.Y = 0;
        };
        animation.Begin();
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
        _isPressActive = false;
        _tiltAnimation?.Stop();
        _contextAnimation?.Stop();
        _reflowAnimation?.Stop();
        _reflowAnimation = null;
        TileReflowTransform.X = TileReflowTransform.Y = 0;
        _tiltAnimation = null;
        _contextAnimation = null;
        _isInReorderContext = false;
        _isSelectedForEditing = false;
        TileTiltProjection.RotationX = TileTiltProjection.RotationY = TileTiltProjection.GlobalOffsetZ = 0;
        TileScaleTransform.ScaleX = TileScaleTransform.ScaleY = 1;
        WindowDialogCoordinator.TryDismissTransient(XamlRoot, TileDetailsFlyout.Hide);
        ObserveCommand();
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
        if (ExecutionProgress is null) { return; }
        bool running = _isLoaded && TileCommand is IAsyncCommandState { IsRunning: true };
        ExecutionProgress.IsActive = running;
        ExecutionProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
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
        _ = VisualStateManager.GoToState(this, IsToggleOn ? "TileOn" : "TileOff", useTransitions);
    }

}
