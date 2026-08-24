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
    private readonly PageLoadSession _loadSession = new();

    /// <summary>Owns the live WebSocket only while this page is visible.</summary>
    private readonly PageLoadSession _streamSession = new();

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Connections(ConnectionsPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Loads current connections when the page becomes visible.</summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Task streamTask = _streamSession.RunAsync(_viewModel.WatchConnectionsAsync);
        await RunRefreshAsync();
        await streamTask;
    }

    /// <summary>Cancels the page-owned refresh when navigation releases the page.</summary>
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadSession.Cancel();
        _streamSession.Cancel();
    }

    private async void RefreshConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunRefreshAsync();
    }

    private async void CloseAllConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(_viewModel.CloseAllConnectionsAsync);
    }

    private async void CloseConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ActiveConnectionDisplayRow row })
        {
            await _loadSession.RunAsync(
                cancellationToken => _viewModel.CloseConnectionAsync(row.Connection, cancellationToken));
        }
    }

    private Task RunRefreshAsync()
    {
        return _loadSession.RunAsync(async cancellationToken =>
        {
            _ = await _viewModel.RefreshConnectionsAsync(cancellationToken);
        });
    }
}
