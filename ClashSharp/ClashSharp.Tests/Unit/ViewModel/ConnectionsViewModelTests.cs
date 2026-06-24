/*
 * Connections ViewModel Tests
 * Verifies active connection refresh and persistence behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/ConnectionsViewModelTests.cs
 * @date: 2026-06-17
 */

using System.Net.Http;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the active connections view model.</summary>
public sealed class ConnectionsViewModelTests
{
    /// <summary>Verifies construction loads labels and initial status text.</summary>
    [Fact]
    public void Constructor_LoadsLabelsAndInitialStatus()
    {
        ConnectionsViewModel viewModel = new(new FakeConnectionsLocalization(), new FakeConnectionClient(), new FakeConnectionLog());

        Assert.Equal("Connections", viewModel.PageTitleText);
        Assert.Equal("Description", viewModel.DescriptionText);
        Assert.Equal("Refresh", viewModel.RefreshConnectionsText);
        Assert.Equal("Persist", viewModel.PersistConnectionsText);
        Assert.Equal("Close all", viewModel.CloseAllConnectionsText);
        Assert.Equal("Close", viewModel.CloseConnectionText);
        Assert.Equal("Not refreshed", viewModel.ConnectionStatusText);
    }

    /// <summary>Verifies refresh success loads rows and updates status.</summary>
    [Fact]
    public async Task RefreshConnectionsAsync_WhenSuccessful_LoadsRows()
    {
        FakeConnectionClient client = new();
        ConnectionsViewModel viewModel = new(new FakeConnectionsLocalization(), client, new FakeConnectionLog());

        await viewModel.RefreshConnectionsAsync(CancellationToken.None);

        Assert.Equal(client.Connections, viewModel.Connections);
        Assert.Equal("2 active", viewModel.ConnectionStatusText);
    }

    /// <summary>Verifies refresh failures clear rows and log a warning.</summary>
    [Fact]
    public async Task RefreshConnectionsAsync_WhenFailure_ClearsRowsAndLogs()
    {
        FakeConnectionClient client = new() { ExceptionToThrow = new HttpRequestException("offline") };
        FakeConnectionLog log = new();
        ConnectionsViewModel viewModel = new(new FakeConnectionsLocalization(), client, log);

        await viewModel.RefreshConnectionsAsync(CancellationToken.None);

        Assert.Empty(viewModel.Connections);
        Assert.Equal("Unavailable", viewModel.ConnectionStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Warning" && entry.Detail == "offline");
    }

    /// <summary>Verifies persisting snapshots refreshes first and logs inserted row count.</summary>
    [Fact]
    public async Task PersistConnectionsAsync_RefreshesAndPersistsSnapshot()
    {
        FakeConnectionClient client = new();
        FakeConnectionLog log = new() { InsertedCount = 2 };
        ConnectionsViewModel viewModel = new(new FakeConnectionsLocalization(), client, log);

        await viewModel.PersistConnectionsAsync(CancellationToken.None);

        Assert.Equal(client.Connections, log.LastSnapshot);
        Assert.Equal("Persisted 2", viewModel.ConnectionStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Detail == "2 rows.");
    }

    /// <summary>Verifies closing one connection calls mihomo and refreshes the visible list.</summary>
    [Fact]
    public async Task CloseConnectionAsync_WhenSuccessful_ClosesConnectionAndRefreshes()
    {
        FakeConnectionClient client = new();
        FakeConnectionLog log = new();
        ConnectionsViewModel viewModel = new(new FakeConnectionsLocalization(), client, log);

        await viewModel.CloseConnectionAsync(client.Connections[0], CancellationToken.None);

        Assert.Equal("1", client.ClosedConnectionId);
        Assert.Equal(1, client.RefreshCount);
        Assert.Equal("Closed", viewModel.ConnectionStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Message == "Active connection closed.");
    }

    /// <summary>Verifies closing all connections calls mihomo and refreshes the visible list.</summary>
    [Fact]
    public async Task CloseAllConnectionsAsync_WhenSuccessful_ClosesAllConnectionsAndRefreshes()
    {
        FakeConnectionClient client = new();
        ConnectionsViewModel viewModel = new(new FakeConnectionsLocalization(), client, new FakeConnectionLog());

        await viewModel.CloseAllConnectionsAsync(CancellationToken.None);

        Assert.True(client.CloseAllCalled);
        Assert.Equal(1, client.RefreshCount);
        Assert.Equal("Closed all", viewModel.ConnectionStatusText);
    }

    /// <summary>Fake localization provider for connection tests.</summary>
    private sealed class FakeConnectionsLocalization : IConnectionsLocalization
    {
        /// <summary>Gets a localized string for a key.</summary>
        /// <param name="key">Localization key. Must not be null.</param>
        /// <returns>Localized test string.</returns>
        public string GetString(string key)
        {
            return key switch
            {
                "Nav.Connections" => "Connections",
                "Page.Connections.Description" => "Description",
                "Command.Refresh" => "Refresh",
                "Command.PersistSnapshot" => "Persist",
                "Command.CloseAll" => "Close all",
                "Command.Close" => "Close",
                "Connections.Status.NotRefreshed" => "Not refreshed",
                "Connections.Status.Active.Format" => "{0} active",
                "Connections.Status.Unavailable" => "Unavailable",
                "Connections.Status.Persisted.Format" => "Persisted {0}",
                "Connections.Status.Closed" => "Closed",
                "Connections.Status.ClosedAll" => "Closed all",
                _ => key,
            };
        }
    }

    /// <summary>Fake active connection client for connection tests.</summary>
    private sealed class FakeConnectionClient : IActiveConnectionClient
    {
        /// <summary>Gets fake active connections.</summary>
        /// <value>Configured active connections.</value>
        public IReadOnlyList<ActiveConnection> Connections { get; } =
        [
            new("1", "proc", "host", "rule", "payload", "proxy", 10, 20, DateTimeOffset.UnixEpoch),
            new("2", "proc", "host", "rule", "payload", "proxy", 30, 40, DateTimeOffset.UnixEpoch),
        ];

        /// <summary>Gets or sets exception thrown on refresh.</summary>
        /// <value>Exception thrown when non-null.</value>
        public Exception? ExceptionToThrow { get; set; }

        /// <summary>Gets the number of refresh calls.</summary>
        /// <value>Refresh call count.</value>
        public int RefreshCount { get; private set; }

        /// <summary>Gets the last closed connection id.</summary>
        /// <value>Closed connection id, or null when none was closed.</value>
        public string? ClosedConnectionId { get; private set; }

        /// <summary>Gets whether close-all was called.</summary>
        /// <value>True when close-all was called.</value>
        public bool CloseAllCalled { get; private set; }

        /// <summary>Gets fake active connections.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured active connections.</returns>
        public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return ExceptionToThrow is null
                ? Task.FromResult(Connections)
                : Task.FromException<IReadOnlyList<ActiveConnection>>(ExceptionToThrow);
        }

        /// <summary>Closes one fake connection.</summary>
        /// <param name="connectionId">Connection id. Must not be null.</param>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Completed task.</returns>
        public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken)
        {
            ClosedConnectionId = connectionId;
            return Task.CompletedTask;
        }

        /// <summary>Closes all fake connections.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Completed task.</returns>
        public Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
        {
            CloseAllCalled = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Fake connection log for connection tests.</summary>
    private sealed class FakeConnectionLog : IConnectionLog
    {
        /// <summary>Gets or sets inserted snapshot row count.</summary>
        /// <value>Inserted row count returned by snapshot append.</value>
        public int InsertedCount { get; set; }

        /// <summary>Gets last persisted snapshot.</summary>
        /// <value>Last snapshot passed to append.</value>
        public IReadOnlyList<ActiveConnection>? LastSnapshot { get; private set; }

        /// <summary>Gets captured log entries.</summary>
        /// <value>Mutable captured log entries.</value>
        public List<LogEntry> Entries { get; } = [];

        /// <summary>Appends a fake connection snapshot.</summary>
        /// <param name="connections">Connections to persist. Must not be null.</param>
        /// <returns>Configured inserted count.</returns>
        public int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections)
        {
            LastSnapshot = connections;
            return InsertedCount;
        }

        /// <summary>Captures one log entry.</summary>
        /// <param name="level">Log level. Must not be null.</param>
        /// <param name="category">Log category. Must not be null.</param>
        /// <param name="message">Log message. Must not be null.</param>
        /// <param name="detail">Optional detail text.</param>
        public void Append(string level, string category, string message, string? detail)
        {
            Entries.Add(new LogEntry(level, category, message, detail));
        }
    }

    /// <summary>Captured log entry.</summary>
    /// <param name="Level">Log level.</param>
    /// <param name="Category">Log category.</param>
    /// <param name="Message">Log message.</param>
    /// <param name="Detail">Optional detail text.</param>
    private sealed record LogEntry(string Level, string Category, string Message, string? Detail);
}
