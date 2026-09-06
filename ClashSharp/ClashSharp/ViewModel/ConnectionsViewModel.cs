using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable view model for active connection monitoring.</summary>
/// <remarks>
/// Invariants: <see cref="Connections"/> is never null after construction.
/// Thread safety: Not thread-safe; intended for UI-thread binding and command execution.
/// Side effects: Commands call injected services that query mihomo and append application logs.
/// </remarks>
internal sealed class ConnectionsViewModel : ObservableObject
{
    private static readonly TimeSpan StreamReconnectDelay = TimeSpan.FromSeconds(1);

    /// <summary>Localization provider used by visible text.</summary>
    private readonly IConnectionsLocalization _localization;

    /// <summary>Active connection client used by refresh commands.</summary>
    private readonly IActiveConnectionClient _connectionClient;

    /// <summary>Log sink used by persistence and warning messages.</summary>
    private readonly IConnectionLog _log;

    /// <summary>Text filter used for UI-only display policy.</summary>
    private readonly Func<string, string> _displayTextFilter;

    /// <summary>Backing field for <see cref="Connections"/>.</summary>
    private IReadOnlyList<ActiveConnectionDisplayRow> _connections = [];
    private IReadOnlyList<ActiveConnectionDisplayRow> _allConnections = [];
    private string _searchText = string.Empty;
    private int _refreshCount;
    private int _closeCount;
    private bool _hasObservation;
    private bool _isAvailable;

    /// <summary>Backing field for <see cref="ConnectionStatusText"/>.</summary>
    private string _connectionStatusText = string.Empty;

    // REST requests, live snapshots and availability transitions share one publication order.
    // A late REST response must not replace a newer request or an already observed stream snapshot.
    private long _snapshotRevision;

    /// <summary>Initializes a connections view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="connectionClient">Active connection client. Must not be null.</param>
    /// <param name="log">Log sink. Must not be null.</param>
    /// <param name="errorSink">Boundary sink for unexpected command failures. Must not be null.</param>
    /// <param name="displayTextFilter">Optional sanitizer applied to controller-sourced display text.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ConnectionsViewModel(
        IConnectionsLocalization localization,
        IActiveConnectionClient connectionClient,
        IConnectionLog log,
        IApplicationErrorSink errorSink,
        Func<string, string>? displayTextFilter = null)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _connectionClient = connectionClient ?? throw new ArgumentNullException(nameof(connectionClient));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentNullException.ThrowIfNull(errorSink);
        _displayTextFilter = displayTextFilter ?? (static text => text);
        ConnectionStatusText = _localization.GetString("Connections.Status.NotRefreshed");
        RefreshConnectionsCommand = new AsyncRelayCommand(
            RefreshConnectionsAsync,
            errorSink,
            operationName: "connections-refresh");
        CloseConnectionCommand = new AsyncRelayCommand(
            CloseConnectionCommandAsync,
            errorSink,
            operationName: "connections-close");
        CloseAllConnectionsCommand = new AsyncRelayCommand(
            CloseAllConnectionsAsync,
            errorSink,
            operationName: "connections-close-all");
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _localization.GetString("Nav.Connections");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _localization.GetString("Page.Connections.Description");

    /// <summary>Gets the refresh command label.</summary>
    /// <value>Localized command label.</value>
    public string RefreshConnectionsText => _localization.GetString("Command.Refresh");

    /// <summary>Gets the close-all command label.</summary>
    /// <value>Localized command label.</value>
    public string CloseAllConnectionsText => _localization.GetString("Command.CloseAll");

    /// <summary>Gets the close-one command label.</summary>
    /// <value>Localized command label.</value>
    public string CloseConnectionText => _localization.GetString("Command.Close");

    /// <summary>Gets the localized description of searchable connection fields.</summary>
    public string SearchPlaceholderText => _localization.GetString("Connections.Search");

    /// <summary>Gets the accessible description of uploaded traffic.</summary>
    public string UploadText => _localization.GetString("Connections.Upload");

    /// <summary>Gets the accessible description of downloaded traffic.</summary>
    public string DownloadText => _localization.GetString("Connections.Download");

    /// <summary>Gets or sets the local filter applied to visible, sanitized connection fields.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            string search = value ?? string.Empty;
            if (SetProperty(ref _searchText, search.Length > 256 ? search[..256] : search))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Gets whether at least one explicit refresh is still running.</summary>
    public bool IsRefreshing => _refreshCount > 0;

    /// <summary>Gets whether a connection close operation is still running.</summary>
    public bool IsClosing => _closeCount > 0;

    /// <summary>Gets close-action availability against the full snapshot, independently of the visible filter.</summary>
    public bool CanCloseConnections => _isAvailable && _allConnections.Count > 0 && !IsClosing;

    /// <summary>Gets whether the page should explain why no rows are displayed.</summary>
    public bool HasEmptyState => !IsRefreshing && Connections.Count == 0;

    /// <summary>Gets a distinct explanation for initial, unavailable, empty, or filtered-empty state.</summary>
    public string EmptyStateText => !_hasObservation
        ? _localization.GetString("Connections.Status.NotRefreshed")
        : !_isAvailable
            ? _localization.GetString("Connections.Status.Unavailable")
            : _localization.GetString(_allConnections.Count == 0 ? "Connections.Empty" : "Connections.NoMatches");

    /// <summary>Gets the displayed row count relative to the full snapshot.</summary>
    public string FilterCountText => string.Format(
        CultureInfo.CurrentCulture,
        _localization.GetString("Connections.FilterCount.Format"),
        Connections.Count,
        _allConnections.Count);

    /// <summary>Gets active connection rows.</summary>
    /// <value>Active connection rows; never null.</value>
    public IReadOnlyList<ActiveConnectionDisplayRow> Connections
    {
        get => _connections;
        private set => SetProperty(ref _connections, value);
    }

    /// <summary>Gets the visible connection status text.</summary>
    /// <value>Status text; never null.</value>
    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set => SetProperty(ref _connectionStatusText, value);
    }

    /// <summary>Gets the command that refreshes active connections.</summary>
    /// <value>Asynchronous refresh command.</value>
    public AsyncRelayCommand RefreshConnectionsCommand { get; }

    /// <summary>Gets the command that closes one active connection.</summary>
    /// <value>Asynchronous close-one command.</value>
    public AsyncRelayCommand CloseConnectionCommand { get; }

    /// <summary>Gets the command that closes all active connections.</summary>
    /// <value>Asynchronous close-all command.</value>
    public AsyncRelayCommand CloseAllConnectionsCommand { get; }

    /// <summary>Refreshes active connections from the local core API.</summary>
    /// <param name="cancellationToken">Cancels the refresh when requested.</param>
    /// <returns>Active connection rows; empty when refresh fails or is superseded.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the connection client.
    /// Thread / reentrancy: UI-thread calls may overlap; only the latest observation is published.
    /// </remarks>
    public async Task<IReadOnlyList<ActiveConnection>> RefreshConnectionsAsync(CancellationToken cancellationToken)
    {
        return await TryRefreshConnectionsAsync(cancellationToken) ?? [];
    }

    /// <summary>Consumes live controller snapshots until the page lifetime is canceled.</summary>
    /// <param name="cancellationToken">Cancels the active socket and reconnect delay.</param>
    /// <returns>A task that completes when cancellation stops the stream.</returns>
    public async Task WatchConnectionsAsync(CancellationToken cancellationToken)
    {
        bool unavailableLogged = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await foreach (IReadOnlyList<ActiveConnection> connections in
                    _connectionClient.StreamActiveConnectionsAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyConnections(connections);
                    unavailableLogged = false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!unavailableLogged)
                {
                    ApplyUnavailableStatus("The mihomo connection stream closed.");
                    unavailableLogged = true;
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or WebSocketException
                    or IOException
                    or JsonException
                    or InvalidOperationException
                && !ExceptionGraphClassifier.IsProcessFatal(exception)
                && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
            {
                if (!unavailableLogged)
                {
                    ApplyUnavailableStatus(exception.Message);
                    unavailableLogged = true;
                }
            }

            await Task.Delay(StreamReconnectDelay, cancellationToken);
        }
    }

    /// <summary>Refreshes active connections and distinguishes an unavailable controller from an empty list.</summary>
    private async Task<IReadOnlyList<ActiveConnection>?> TryRefreshConnectionsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long revision = ++_snapshotRevision;
        _refreshCount++;
        NotifyViewState();
        try
        {
            IReadOnlyList<ActiveConnection> connections = await _connectionClient.GetActiveConnectionsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _snapshotRevision)
            {
                return null;
            }

            ApplyConnections(connections);
            return connections;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            if (revision == _snapshotRevision)
            {
                ApplyUnavailableStatus(exception.Message);
            }

            return null;
        }
        finally
        {
            _refreshCount--;
            NotifyViewState();
        }
    }

    private void ApplyConnections(IReadOnlyList<ActiveConnection> connections)
    {
        ++_snapshotRevision;
        _hasObservation = true;
        _isAvailable = true;
        _allConnections = connections
            .Select(connection => new ActiveConnectionDisplayRow(connection, _displayTextFilter))
            .ToArray();
        ApplyFilter();
        ConnectionStatusText = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Connections.Status.Active.Format"),
            connections.Count);
    }

    private void ApplyUnavailableStatus(string detail)
    {
        ++_snapshotRevision;
        _hasObservation = true;
        _isAvailable = false;
        _allConnections = [];
        ApplyFilter();
        ConnectionStatusText = _localization.GetString("Connections.Status.Unavailable");
        _log.Append("Warning", "Connections", ConnectionStatusText, detail);
    }

    private void ApplyFilter()
    {
        string query = SearchText.Trim();
        Connections = query.Length == 0
            ? _allConnections
            : _allConnections.Where(row =>
                row.ProcessNameDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.HostDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.RuleDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.ProxyNameDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        NotifyViewState();
    }

    private void NotifyViewState()
    {
        OnPropertyChanged(nameof(IsRefreshing));
        OnPropertyChanged(nameof(IsClosing));
        OnPropertyChanged(nameof(CanCloseConnections));
        OnPropertyChanged(nameof(HasEmptyState));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(FilterCountText));
    }

    /// <summary>Closes one active connection and refreshes the visible list.</summary>
    /// <param name="connection">Connection to close.</param>
    /// <param name="cancellationToken">Cancels the close or refresh request.</param>
    /// <returns>A task that completes after close and refresh finish.</returns>
    public async Task CloseConnectionAsync(ActiveConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _closeCount++;
        NotifyViewState();
        try
        {
            await _connectionClient.CloseConnectionAsync(connection.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryRefreshConnectionsAsync(cancellationToken) is null)
            {
                return;
            }

            ConnectionStatusText = _localization.GetString("Connections.Status.Closed");
            _log.Append("Info", "Connections", ConnectionStatusText, connection.Id);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            ConnectionStatusText = _localization.GetString("Connections.Status.Unavailable");
            _log.Append("Warning", "Connections", ConnectionStatusText, exception.Message);
        }
        finally
        {
            _closeCount--;
            NotifyViewState();
        }
    }

    /// <summary>Closes all active connections and refreshes the visible list.</summary>
    /// <param name="cancellationToken">Cancels the close or refresh request.</param>
    /// <returns>A task that completes after close and refresh finish.</returns>
    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _closeCount++;
        NotifyViewState();
        try
        {
            await _connectionClient.CloseAllConnectionsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryRefreshConnectionsAsync(cancellationToken) is null)
            {
                return;
            }

            ConnectionStatusText = _localization.GetString("Connections.Status.ClosedAll");
            _log.Append("Info", "Connections", ConnectionStatusText, null);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            ConnectionStatusText = _localization.GetString("Connections.Status.Unavailable");
            _log.Append("Warning", "Connections", ConnectionStatusText, exception.Message);
        }
        finally
        {
            _closeCount--;
            NotifyViewState();
        }
    }

    /// <summary>Closes one active connection from a command parameter.</summary>
    private Task CloseConnectionCommandAsync(object? parameter, CancellationToken cancellationToken)
    {
        return parameter switch
        {
            ActiveConnectionDisplayRow row => CloseConnectionAsync(row.Connection, cancellationToken),
            ActiveConnection connection => CloseConnectionAsync(connection, cancellationToken),
            _ => Task.CompletedTask,
        };
    }
}
