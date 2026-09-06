using System;
using System.Threading.Tasks;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for monitoring and managing active network connections.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="ConnectionsViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Opens a page-owned local-controller WebSocket while visible and can close connections.
/// </remarks>
public sealed partial class Connections : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly ConnectionsViewModel _viewModel;

    /// <summary>Owns the cancellable initial refresh while this page is loaded.</summary>
    private readonly PageLoadSession _loadSession;

    /// <summary>Owns the live WebSocket only while this page is visible.</summary>
    private readonly PageLoadSession _streamSession;

    private readonly PageOperationSession _operations;

    private bool _isLoaded;

    private int _visit;

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Connections(ConnectionsPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _loadSession = new PageLoadSession(dependencies.ErrorSink, "connections-page-refresh");
        _streamSession = new PageLoadSession(dependencies.ErrorSink, "connections-page-stream");
        _operations = new PageOperationSession(dependencies.ErrorSink, "connections-page-close");
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Loads current connections when the page becomes visible.</summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        int visit = ++_visit;
        _isLoaded = true;
        await _operations.DrainAsync();
        if (!_isLoaded || visit != _visit)
        {
            return;
        }

        Task streamTask = _streamSession.RunAsync(_viewModel.WatchConnectionsAsync);
        await RunRefreshAsync();
        await streamTask;
    }

    /// <summary>Cancels the page-owned refresh when navigation releases the page.</summary>
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        ++_visit;
        _loadSession.Cancel();
        _streamSession.Cancel();
        _operations.Cancel();
    }

    private async void RefreshConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunRefreshAsync();
    }

    private async void CloseAllConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            await _operations.RunAsync(_viewModel.CloseAllConnectionsAsync);
        }
    }

    private async void CloseConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoaded && sender is FrameworkElement { DataContext: ActiveConnectionDisplayRow row })
        {
            await _operations.RunAsync(
                cancellationToken => _viewModel.CloseConnectionAsync(row.Connection, cancellationToken));
        }
    }

    private Task RunRefreshAsync()
    {
        if (!_isLoaded)
        {
            return Task.CompletedTask;
        }

        return _loadSession.RunAsync(async cancellationToken =>
        {
            _ = await _viewModel.RefreshConnectionsAsync(cancellationToken);
        });
    }
}
