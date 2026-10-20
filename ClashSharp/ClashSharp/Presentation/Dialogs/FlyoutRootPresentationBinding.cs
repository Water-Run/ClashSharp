using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Keeps an owned flyout aligned with its live window without replacing content or replaying commands.</summary>
/// <remarks>All callbacks and disposal belong to the UI thread. Closing or unloading releases every root subscription.</remarks>
internal sealed class FlyoutRootPresentationBinding : IDisposable
{
    private readonly XamlRoot _xamlRoot;
    private readonly FrameworkElement _owner;
    private readonly FrameworkElement _content;
    private readonly Action _updateBounds;
    private FrameworkElement? _root;
    private Flyout? _flyout;
    private FlyoutPresenter? _presenter;
    private long? _languageToken;
    private long? _directionToken;
    private bool _shown;

    public FlyoutRootPresentationBinding(Flyout flyout, FrameworkElement owner, FrameworkElement content, Action updateBounds)
    {
        _flyout = flyout ?? throw new ArgumentNullException(nameof(flyout));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _updateBounds = updateBounds ?? throw new ArgumentNullException(nameof(updateBounds));
        _xamlRoot = owner.XamlRoot ?? throw new InvalidOperationException("An anchored flyout requires a live visual root.");
        _root = _xamlRoot.Content as FrameworkElement
            ?? throw new InvalidOperationException("An anchored flyout requires a framework root.");
        try
        {
            Style style = new(typeof(FlyoutPresenter)) { BasedOn = flyout.FlyoutPresenterStyle };
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, _root.ActualTheme));
            style.Setters.Add(new Setter(FrameworkElement.LanguageProperty, _root.Language));
            style.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, _root.FlowDirection));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, GetPresenterMaxWidth()));
            flyout.FlyoutPresenterStyle = style;
            flyout.Opening += OnOpening;
            flyout.Opened += OnOpened;
            flyout.Closed += OnClosed;
            _owner.Unloaded += OnOwnerUnloaded;
            _content.Loaded += OnContentLoaded;
            _xamlRoot.Changed += OnRootChanged;
            _languageToken = _root.RegisterPropertyChangedCallback(FrameworkElement.LanguageProperty, OnPresentationChanged);
            _directionToken = _root.RegisterPropertyChangedCallback(FrameworkElement.FlowDirectionProperty, OnPresentationChanged);
            _root.ActualThemeChanged += OnThemeChanged;
            Refresh();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private double GetPresenterMaxWidth() => double.IsFinite(_xamlRoot.Size.Width) && _xamlRoot.Size.Width > 0
        ? Math.Max(1, _xamlRoot.Size.Width - 32) : 640;

    private void OnOpening(object? sender, object args) { _shown = true; Refresh(); }
    private void OnOpened(object? sender, object args) => Refresh();
    private void OnContentLoaded(object sender, RoutedEventArgs args) => Refresh();
    private void OnClosed(object? sender, object args) { _shown = false; Dispose(); }
    private void OnOwnerUnloaded(object sender, RoutedEventArgs args) => Dispose();
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Refresh();
    private void OnPresentationChanged(DependencyObject sender, DependencyProperty property) => Refresh();
    private void OnThemeChanged(FrameworkElement sender, object args) => Refresh();

    private void FindPresenter()
    {
        // Work with the ordinary framework presenter; no derived native control or protected template access.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(_content); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is FlyoutPresenter presenter) { _presenter = presenter; break; }
        }
    }

    private void Refresh()
    {
        FrameworkElement? root = _root;
        if (root is null) { return; }
        WindowDialogCoordinator.TryRefreshWindowPresentation(_xamlRoot, () =>
        {
            if (_presenter is null) { FindPresenter(); }
            _content.RequestedTheme = root.ActualTheme;
            _content.Language = root.Language;
            _content.FlowDirection = root.FlowDirection;
            if (_presenter is FlyoutPresenter presenter)
            {
                presenter.RequestedTheme = root.ActualTheme;
                presenter.Language = root.Language;
                presenter.FlowDirection = root.FlowDirection;
                presenter.MaxWidth = GetPresenterMaxWidth();
            }
            _updateBounds();
        });
    }

    public void Dispose()
    {
        FrameworkElement? root = _root;
        _root = null;
        if (root is null) { return; }
        Flyout? flyout = _flyout;
        _flyout = null;
        _presenter = null;
        _owner.Unloaded -= OnOwnerUnloaded;
        _content.Loaded -= OnContentLoaded;
        _xamlRoot.Changed -= OnRootChanged;
        root.ActualThemeChanged -= OnThemeChanged;
        if (_languageToken is long language) { root.UnregisterPropertyChangedCallback(FrameworkElement.LanguageProperty, language); }
        if (_directionToken is long direction) { root.UnregisterPropertyChangedCallback(FrameworkElement.FlowDirectionProperty, direction); }
        if (flyout is not null)
        {
            flyout.Opening -= OnOpening;
            flyout.Opened -= OnOpened;
            flyout.Closed -= OnClosed;
            if (_shown) { _shown = false; WindowDialogCoordinator.TryRefreshWindowPresentation(_xamlRoot, flyout.Hide); }
        }
    }
}
