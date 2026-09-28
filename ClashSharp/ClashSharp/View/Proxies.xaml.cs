using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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
            && FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard })
        {
            list.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
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
