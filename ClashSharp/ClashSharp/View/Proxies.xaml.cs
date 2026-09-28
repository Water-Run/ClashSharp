using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ClashSharp.View;

/// <summary>Page for managing proxy groups and individual proxy nodes.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="ProxiesViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Refreshes runtime state when loaded and delegates user selections to the view model.
/// </remarks>
public sealed partial class Proxies : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly ProxiesViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();

    private readonly PageOperationSession _selectionSession;

    private readonly HashSet<AsyncRelayCommand> _pendingCommands = [];

    private bool _isLoaded;

    private int _visit;

    private bool _updatingProvider;

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Proxies(ProxiesPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _selectionSession = new PageOperationSession(dependencies.ErrorSink, "proxies-page-selection");
        InitializeComponent();
        DataContext = _viewModel;
        foreach (ListView list in new[] { ProxyGroupsList, ProxyNodesList, ProviderResourcesList })
        {
            list.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(List_PointerWheelChanged), true);
        }
    }

    /// <summary>Loads catalog and mihomo runtime state while the page is active.</summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        int visit = ++_visit;
        _isLoaded = true;
        await _selectionSession.DrainAsync();
        if (!_isLoaded || visit != _visit)
        {
            return;
        }

        await _selectionSession.RunAsync(token =>
            _loadSession.RunAsync(_viewModel.LoadAsync, cancellationToken: token));
    }

    /// <summary>Cancels page-owned requests before the visual tree is released.</summary>
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        ++_visit;
        _loadSession.Cancel();
        _selectionSession.Cancel();
    }

    private async void RefreshNodes_Click(object sender, RoutedEventArgs e)
    {
        await RunPageCommandAsync(_viewModel.RefreshNodesCommand);
    }

    private async void TestLatency_Click(object sender, RoutedEventArgs e)
    {
        await RunPageCommandAsync(_viewModel.TestLatencyCommand);
    }

    private async void RefreshRuntime_Click(object sender, RoutedEventArgs e)
    {
        await RunPageCommandAsync(_viewModel.RefreshRuntimeCommand);
    }

    /// <summary>Owns toolbar work until completion and coalesces repeated clicks without moving focus.</summary>
    private async Task RunPageCommandAsync(AsyncRelayCommand command)
    {
        if (!_isLoaded || !command.CanExecute(null) || !_pendingCommands.Add(command))
        {
            return;
        }

        try
        {
            await _selectionSession.RunAsync(token => command.ExecuteAsync(null, token));
        }
        finally
        {
            _pendingCommands.Remove(command);
        }
    }

    /// <summary>Keeps a bounded list visible in the outer page while its items receive keyboard focus.</summary>
    private void List_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ListView list
            && FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard } focused)
        {
            int visit = _visit;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_isLoaded || visit != _visit
                    || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), focused)
                    || focused.FocusState != FocusState.Keyboard)
                {
                    return;
                }

                // Virtualized rows can grow after a resize or language change. Bring the actual
                // focused control into view after measuring it, including both nested scrollers.
                list.UpdateLayout();
                focused.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
        }
    }

    /// <summary>Lets the native outer scroller consume a wheel tick when a bounded list reaches its edge.</summary>
    private void List_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListView list || !e.Handled)
        {
            return;
        }

        // Nested pickers and scrollbars own their input; only chain the list's own vertical wheel input.
        for (DependencyObject? source = e.OriginalSource as DependencyObject;
            source is not null && source != list; source = VisualTreeHelper.GetParent(source))
        {
            if (source is ComboBox or ScrollBar)
            {
                return;
            }
        }

        var properties = e.GetCurrentPoint(list).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0
            || FindScrollViewer(list) is not ScrollViewer scroll)
        {
            return;
        }

        bool atEdge = properties.MouseWheelDelta > 0
            ? scroll.VerticalOffset <= 0.5
            : scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5;
        if (atEdge)
        {
            // ScrollViewer's built-in chaining excludes mouse wheels. Resume bubbling instead of
            // inventing a pixel step, so Windows keeps its configured wheel speed and animation.
            e.Handled = false;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll)
        {
            return scroll;
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is ScrollViewer child)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Handles runtime strategy group selection changes.</summary>
    private async void ProxyGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded
            || sender is not ComboBox { DataContext: MihomoProxyGroupDisplay group, SelectedItem: string proxyName }
            || string.Equals(group.CurrentSelection, proxyName, StringComparison.Ordinal))
        {
            return;
        }

        await _selectionSession.RunAsync(
            cancellationToken => _viewModel.SelectProxyAsync(group.Model, proxyName, cancellationToken));
    }

    /// <summary>Keeps the update control focused while admitting only one page-owned update.</summary>
    private async void UpdateProvider_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded || _updatingProvider
            || sender is not Button { DataContext: MihomoProviderResourceDisplay provider }
            || !_viewModel.UpdateProviderCommand.CanExecute(provider))
        {
            return;
        }

        _updatingProvider = true;
        try
        {
            await _selectionSession.RunAsync(
                token => _viewModel.UpdateProviderCommand.ExecuteAsync(provider, token));
        }
        finally
        {
            _updatingProvider = false;
        }
    }
}
