using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Owns one native modal window and XAML island for a managed ContentDialog presentation.</summary>
/// <remarks>
/// Created, shown and disposed on the UI thread. The native owner is assigned at window creation;
/// its size never limits the dialog. The coordinator retains admission and cancellation ownership.
/// </remarks>
internal sealed class OwnedDialogHost : IDisposable
{
    private static readonly ConcurrentDictionary<ulong, byte> WindowIds = new();
    private readonly ContentDialog _dialog;
    private readonly XamlRoot _ownerRoot;
    private readonly Grid _root;
    private readonly List<(DependencyProperty Property, long Token)> _presentationTokens = [];
    private AppWindow? _window;
    private AppWindow? _owner;
    private DesktopWindowXamlSource? _source;
    private FrameworkElement? _card;
    private bool _disposed;
    private bool _destroyed;
    private bool _sizing;
    private bool _hiddenWithOwner;
    private ulong _windowId;
    private readonly CancellationTokenSource _ownerClosing = new();

    private OwnedDialogHost(ContentDialog dialog, XamlRoot ownerRoot, Grid root)
    {
        _dialog = dialog;
        _ownerRoot = ownerRoot;
        _root = root;
    }

    internal XamlRoot DialogRoot => _root.XamlRoot
        ?? throw new InvalidOperationException("The native dialog host has no XAML root.");

    internal CancellationToken OwnerClosing => _ownerClosing.Token;

    internal static bool IsForeground
    {
        get
        {
            if (WindowIds.IsEmpty) { return false; }
            nint foreground = GetForegroundWindow();
            return foreground != 0 && WindowIds.ContainsKey(Win32Interop.GetWindowIdFromWindow(foreground).Value);
        }
    }

    internal static OwnedDialogHost Create(ContentDialog dialog, XamlRoot ownerRoot)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(ownerRoot);
        Grid root = (Grid)XamlReader.Load("<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource ContentDialogBackground}\" />");
        var host = new OwnedDialogHost(dialog, ownerRoot, root);
        try { host.Initialize(); return host; }
        catch { host.Dispose(); throw; }
    }

    private void Initialize()
    {
        var ownerId = _ownerRoot.ContentIslandEnvironment.AppWindowId;
        _owner = AppWindow.GetFromWindowId(ownerId)
            ?? throw new InvalidOperationException("A dialog requires a live native owner.");
        OverlappedPresenter presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsModal = true;
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        _window = AppWindow.Create(presenter, ownerId);
        _windowId = _window.Id.Value;
        WindowIds.TryAdd(_windowId, 0);
        try { _window.IsShownInSwitchers = false; }
        catch (NotImplementedException)
        {
            // Shell switchers may be unavailable on a private desktop or during shell startup.
            // Native ownership still controls modality and lifetime without this cosmetic setting.
        }
        _window.Title = _dialog.Title?.ToString() ?? string.Empty;
        _window.Closing += OnClosing;
        _window.Destroying += OnDestroying;
        _window.Changed += OnWindowChanged;
        _owner.Changed += OnOwnerChanged;

        foreach (DependencyProperty property in new[] { FrameworkElement.RequestedThemeProperty,
            FrameworkElement.LanguageProperty, FrameworkElement.FlowDirectionProperty, ContentDialog.TitleProperty })
        {
            _presentationTokens.Add((property, _dialog.RegisterPropertyChangedCallback(property, OnPresentationChanged)));
        }
        RefreshPresentation();

        double preferredWidth = _dialog.Resources.TryGetValue("ContentDialogMaxWidth", out object value)
            && value is double maximum && double.IsFinite(maximum) ? maximum : 548;
        var available = DialogViewport.GetAvailableSize(_ownerRoot);
        Resize(preferredWidth + 64, available.Height);
        _source = new DesktopWindowXamlSource();
        _source.Initialize(_window.Id);
        _source.Content = _root;
        if (_source.SiteBridge is DesktopChildSiteBridge bridge)
        {
            bridge.ResizePolicy = ContentSizePolicy.ResizeContentToParentWindow;
        }
        ResizeIsland();
        _source.SiteBridge.Show();
        _root.Children.Add(_dialog);
        _dialog.Opened += OnOpened;
    }

    internal void Show() => _window!.Show();

    internal void DismissForOwnerClose()
    {
        Dismiss();
        _ownerClosing.Cancel();
    }

    private void Dismiss()
    {
        if (_disposed || _destroyed) { return; }
        try { _dialog.Hide(); }
        catch (COMException exception) when (exception.HResult is unchecked((int)0x80000013) or unchecked((int)0x80004005))
        {
            // The native owner may already have destroyed its owned window during shutdown.
        }
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _card = FindCard(_dialog);
        if (_card is not null) { _card.SizeChanged += OnCardSizeChanged; }
        FitCard();
    }

    private void OnCardSizeChanged(object sender, SizeChangedEventArgs args) => FitCard();

    private void OnPresentationChanged(DependencyObject sender, DependencyProperty property) => RefreshPresentation();

    private void RefreshPresentation()
    {
        if (_disposed || _destroyed) { return; }
        _root.RequestedTheme = _dialog.RequestedTheme;
        _root.Language = _dialog.Language;
        _root.FlowDirection = _dialog.FlowDirection;
        if (_window is not null)
        {
            _window.Title = _dialog.Title?.ToString() ?? string.Empty;
            _window.TitleBar.PreferredTheme = _dialog.RequestedTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        }
    }

    private void FitCard()
    {
        if (_sizing || _disposed || _destroyed || _card is not { ActualWidth: > 0, ActualHeight: > 0 }) { return; }
        _sizing = true;
        try { Resize(_card.ActualWidth + 64, _card.ActualHeight + 64); }
        finally { _sizing = false; }
    }

    private void Resize(double width, double height)
    {
        if (_window is null || _owner is null || _destroyed) { return; }
        RectInt32 work = DisplayArea.GetFromWindowId(_owner.Id, DisplayAreaFallback.Nearest).WorkArea;
        PointInt32 anchor = _owner.Position;
        SizeInt32 ownerSize = _owner.Size;
        double scale = _root.XamlRoot?.RasterizationScale ?? _ownerRoot.RasterizationScale;
        DialogWindowBoundsPolicy.Bounds bounds = DialogWindowBoundsPolicy.Place(
            new(work.X, work.Y, work.Width, work.Height),
            new(anchor.X, anchor.Y, ownerSize.Width, ownerSize.Height), scale, width, height);
        if (_window.Position.X != bounds.X || _window.Position.Y != bounds.Y
            || _window.Size.Width != bounds.Width || _window.Size.Height != bounds.Height)
        {
            _window.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }
        ResizeIsland();
    }

    private void ResizeIsland()
    {
        if (_source is null || _window is null || _destroyed) { return; }
        SizeInt32 client = _window.ClientSize;
        _source.SiteBridge.MoveAndResize(new RectInt32(0, 0, client.Width, client.Height));
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_disposed && !_destroyed && args.DidSizeChange) { ResizeIsland(); }
    }

    private void OnOwnerChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_disposed || _destroyed || _window is null) { return; }
        bool hidden = !sender.IsVisible || sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (hidden && !_hiddenWithOwner) { _window.Hide(); }
        else if (!hidden && _hiddenWithOwner) { _window.Show(); }
        _hiddenWithOwner = hidden;
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        Dismiss();
    }

    private void OnDestroying(AppWindow sender, object args)
    {
        Dismiss();
        _destroyed = true;
        _ownerClosing.Cancel();
        WindowIds.TryRemove(_windowId, out _);
    }

    private static FrameworkElement? FindCard(DependencyObject element)
    {
        if (element is FrameworkElement { Name: "BackgroundElement" } card) { return card; }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            FrameworkElement? found = FindCard(VisualTreeHelper.GetChild(element, index));
            if (found is not null) { return found; }
        }
        return null;
    }

    /// <summary>Detaches the dialog before releasing its XAML island and owned native window.</summary>
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        WindowIds.TryRemove(_windowId, out _);
        foreach ((DependencyProperty property, long token) in _presentationTokens) { _dialog.UnregisterPropertyChangedCallback(property, token); }
        _presentationTokens.Clear();
        if (_owner is not null) { _owner.Changed -= OnOwnerChanged; }
        _dialog.Opened -= OnOpened;
        if (_card is not null) { _card.SizeChanged -= OnCardSizeChanged; _card = null; }
        _root.Children.Clear();
        if (_window is not null)
        {
            _window.Closing -= OnClosing;
            _window.Destroying -= OnDestroying;
            _window.Changed -= OnWindowChanged;
        }
        _source?.Dispose();
        _source = null;
        if (_window is not null && !_destroyed) { _window.Destroy(); }
        _window = null;
        _ownerClosing.Dispose();
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();
}
