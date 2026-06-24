/*
 * Connections ViewModel
 * Owns bindable active connection state and commands
 *
 * @author: WaterRun
 * @file: ViewModel/ConnectionsViewModel.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Localization contract required by <see cref="ConnectionsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations return a non-null string for every requested key.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: None required by the contract.
/// </remarks>
internal interface IConnectionsLocalization
{
    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Resolved localized string or fallback text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    string GetString(string key);
}

/// <summary>Active connection API contract used by <see cref="ConnectionsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Returned rows are safe to bind directly.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May call the local mihomo external controller.
/// </remarks>
internal interface IActiveConnectionClient
{
    /// <summary>Gets active connection rows.</summary>
    /// <param name="cancellationToken">Cancels the local API request when requested.</param>
    /// <returns>Active connection rows.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the underlying request.
    /// Completion semantics: Does not persist returned rows.
    /// </remarks>
    Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken);

    /// <summary>Closes one active connection.</summary>
    /// <param name="connectionId">Connection id. Must not be null or empty.</param>
    /// <param name="cancellationToken">Cancels the local API request when requested.</param>
    /// <returns>A task that completes after the connection is closed.</returns>
    Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken);

    /// <summary>Closes all active connections.</summary>
    /// <param name="cancellationToken">Cancels the local API request when requested.</param>
    /// <returns>A task that completes after mihomo closes all connections.</returns>
    Task CloseAllConnectionsAsync(CancellationToken cancellationToken);
}

/// <summary>Connection logging contract used by <see cref="ConnectionsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Snapshot append returns the number of inserted rows.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May write snapshots and logs to persistent storage.
/// </remarks>
internal interface IConnectionLog
{
    /// <summary>Appends active connection snapshot rows.</summary>
    /// <param name="connections">Connections to persist. Must not be null.</param>
    /// <returns>Number of inserted rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connections"/> is null.</exception>
    int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections);

    /// <summary>Appends one log entry.</summary>
    /// <param name="level">Log level. Must not be null.</param>
    /// <param name="category">Log category. Must not be null.</param>
    /// <param name="message">Log summary. Must not be null.</param>
    /// <param name="detail">Optional detail text.</param>
    void Append(string level, string category, string message, string? detail);
}

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

    /// <summary>Backing field for <see cref="Connections"/>.</summary>
    private IReadOnlyList<ActiveConnection> _connections = [];

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
        IConnectionLog log)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _connectionClient = connectionClient ?? throw new ArgumentNullException(nameof(connectionClient));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ConnectionStatusText = _localization.GetString("Connections.Status.NotRefreshed");
        RefreshConnectionsCommand = new AsyncRelayCommand(RefreshConnectionsAsync);
        PersistConnectionsCommand = new AsyncRelayCommand(PersistConnectionsAsync);
        CloseConnectionCommand = new AsyncRelayCommand(CloseConnectionCommandAsync);
        CloseAllConnectionsCommand = new AsyncRelayCommand(CloseAllConnectionsAsync);
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
    public IReadOnlyList<ActiveConnection> Connections
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
            Connections = connections;
            ConnectionStatusText = string.Format(_localization.GetString("Connections.Status.Active.Format"), connections.Count);
            return connections;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            Connections = [];
            ConnectionStatusText = _localization.GetString("Connections.Status.Unavailable");
            _log.Append("Warning", "Connections", "Active connection refresh failed.", exception.Message);
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
        ConnectionStatusText = string.Format(_localization.GetString("Connections.Status.Persisted.Format"), insertedCount);
        _log.Append("Info", "Connections", "Active connection snapshot persisted.", $"{insertedCount:N0} rows.");
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
            _log.Append("Info", "Connections", "Active connection closed.", connection.Id);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            ConnectionStatusText = _localization.GetString("Connections.Status.Unavailable");
            _log.Append("Warning", "Connections", "Active connection close failed.", exception.Message);
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
            _log.Append("Info", "Connections", "All active connections closed.", null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            ConnectionStatusText = _localization.GetString("Connections.Status.Unavailable");
            _log.Append("Warning", "Connections", "Active connection close-all failed.", exception.Message);
        }
    }

    /// <summary>Closes one active connection from a command parameter.</summary>
    private Task CloseConnectionCommandAsync(object? parameter, CancellationToken cancellationToken)
    {
        return parameter is ActiveConnection connection
            ? CloseConnectionAsync(connection, cancellationToken)
            : Task.CompletedTask;
    }
}
