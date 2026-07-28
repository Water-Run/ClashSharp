using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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
/// Side effects: Commands call injected services that may query mihomo and write SQLite rows.
/// </remarks>
internal sealed class ConnectionsViewModel : ObservableObject
{
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

    /// <summary>Backing field for <see cref="ConnectionStatusText"/>.</summary>
    private string _connectionStatusText = string.Empty;

    /// <summary>Initializes a connections view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="connectionClient">Active connection client. Must not be null.</param>
    /// <param name="log">Log sink. Must not be null.</param>
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
        PersistConnectionsCommand = new AsyncRelayCommand(
            PersistConnectionsAsync,
            errorSink,
            operationName: "connections-persist");
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

    /// <summary>Gets the persist snapshot command label.</summary>
    /// <value>Localized command label.</value>
    public string PersistConnectionsText => _localization.GetString("Command.PersistSnapshot");

    /// <summary>Gets the close-all command label.</summary>
    /// <value>Localized command label.</value>
    public string CloseAllConnectionsText => _localization.GetString("Command.CloseAll");

    /// <summary>Gets the close-one command label.</summary>
    /// <value>Localized command label.</value>
    public string CloseConnectionText => _localization.GetString("Command.Close");

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

    /// <summary>Gets the command that persists a refreshed connection snapshot.</summary>
    /// <value>Asynchronous persistence command.</value>
    public AsyncRelayCommand PersistConnectionsCommand { get; }

    /// <summary>Gets the command that closes one active connection.</summary>
    /// <value>Asynchronous close-one command.</value>
    public AsyncRelayCommand CloseConnectionCommand { get; }

    /// <summary>Gets the command that closes all active connections.</summary>
    /// <value>Asynchronous close-all command.</value>
    public AsyncRelayCommand CloseAllConnectionsCommand { get; }

    /// <summary>Refreshes active connections from the local core API.</summary>
    /// <param name="cancellationToken">Cancels the refresh when requested.</param>
    /// <returns>Active connection rows; empty when refresh fails.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the connection client.
    /// Thread / reentrancy: UI callers should use <see cref="RefreshConnectionsCommand"/> to prevent reentrancy.
    /// </remarks>
    public async Task<IReadOnlyList<ActiveConnection>> RefreshConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ActiveConnection> connections = await _connectionClient.GetActiveConnectionsAsync(cancellationToken);
            Connections = connections.Select(connection => new ActiveConnectionDisplayRow(connection, _displayTextFilter)).ToArray();
            ConnectionStatusText = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Connections.Status.Active.Format"), connections.Count);
            return connections;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            Connections = [];
            ConnectionStatusText = _localization.GetString("Connections.Status.Unavailable");
            _log.Append("Warning", "Connections", _localization.GetString("Connections.Status.Unavailable"), exception.Message);
            return [];
        }
    }

    /// <summary>Refreshes active connections and persists them as a snapshot.</summary>
    /// <param name="cancellationToken">Cancels the refresh when requested.</param>
    /// <returns>A task that completes after persistence and logging finish.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the refresh operation.
    /// Thread / reentrancy: UI callers should use <see cref="PersistConnectionsCommand"/> to prevent reentrancy.
    /// </remarks>
    public async Task PersistConnectionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveConnection> connections = await RefreshConnectionsAsync(cancellationToken);
        int insertedCount = _log.AppendConnectionSnapshot(connections);
        ConnectionStatusText = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Connections.Status.Persisted.Format"), insertedCount);
        _log.Append("Info", "Connections", ConnectionStatusText, null);
    }

    /// <summary>Closes one active connection and refreshes the visible list.</summary>
    /// <param name="connection">Connection to close.</param>
    /// <param name="cancellationToken">Cancels the close or refresh request.</param>
    /// <returns>A task that completes after close and refresh finish.</returns>
    public async Task CloseConnectionAsync(ActiveConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await _connectionClient.CloseConnectionAsync(connection.Id, cancellationToken);
            await RefreshConnectionsAsync(cancellationToken);
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
    }

    /// <summary>Closes all active connections and refreshes the visible list.</summary>
    /// <param name="cancellationToken">Cancels the close or refresh request.</param>
    /// <returns>A task that completes after close and refresh finish.</returns>
    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connectionClient.CloseAllConnectionsAsync(cancellationToken);
            await RefreshConnectionsAsync(cancellationToken);
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
