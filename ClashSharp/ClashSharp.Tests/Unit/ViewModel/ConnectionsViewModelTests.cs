using System.Net.Http;
using System.Runtime.CompilerServices;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the active connections view model.</summary>
public sealed class ConnectionsViewModelTests
{
    [Theory]
    [InlineData("browser")]
    [InlineData("EXAMPLE")]
    [InlineData("payload-a")]
    [InlineData("route a")]
    public async Task SearchText_FiltersDisplayedFieldsWithoutRequestingAnotherSnapshot(string query)
    {
        FakeConnectionClient client = new()
        {
            Connections =
            [
                new("1", "Browser", "example.test", "rule", "payload-a", "Route A", 1, 2, DateTimeOffset.UnixEpoch),
                new("2", "curl", "other.test", "direct", "", "DIRECT", 3, 4, DateTimeOffset.UnixEpoch),
            ],
        };
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(), client, new FakeConnectionLog(), new TestApplicationErrorSink());
        await viewModel.RefreshConnectionsAsync(CancellationToken.None);

        viewModel.SearchText = query;

        Assert.Equal("1", Assert.Single(viewModel.Connections).Connection.Id);
        Assert.Equal("1 of 2", viewModel.FilterCountText);
        Assert.Equal(1, client.RefreshCount);
        viewModel.SearchText = string.Empty;
        Assert.Equal(2, viewModel.Connections.Count);
        Assert.Equal(1, client.RefreshCount);
    }

    [Fact]
    public async Task SearchText_UsesSanitizedTextAndDoesNotChangeCloseAllScope()
    {
        FakeConnectionClient client = new();
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(), client, new FakeConnectionLog(), new TestApplicationErrorSink(),
            text => text.Replace("host", "visible-destination", StringComparison.Ordinal));
        await viewModel.RefreshConnectionsAsync(CancellationToken.None);
        viewModel.SearchText = "host";
        Assert.Empty(viewModel.Connections);
        Assert.True(viewModel.CanCloseConnections);
        Assert.Equal("No matches", viewModel.EmptyStateText);

        await viewModel.CloseAllConnectionsAsync(CancellationToken.None);
        Assert.True(client.CloseAllCalled);
        Assert.False(viewModel.IsClosing);
        viewModel.SearchText = "VISIBLE-DESTINATION";
        Assert.Equal(2, viewModel.Connections.Count);
    }

    [Fact]
    public async Task EmptyState_DistinguishesInitialEmptyAndUnavailableSnapshots()
    {
        FakeConnectionClient client = new() { Connections = [] };
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(), client, new FakeConnectionLog(), new TestApplicationErrorSink());
        Assert.Equal("Not refreshed", viewModel.EmptyStateText);
        await viewModel.RefreshConnectionsAsync(CancellationToken.None);
        Assert.Equal("No active connections", viewModel.EmptyStateText);
        Assert.True(viewModel.HasEmptyState);
        Assert.False(viewModel.CanCloseConnections);

        client.ExceptionToThrow = new HttpRequestException("Controller unavailable.");
        await viewModel.RefreshConnectionsAsync(CancellationToken.None);
        Assert.Equal("Unavailable", viewModel.EmptyStateText);
        Assert.False(viewModel.IsRefreshing);
        Assert.True(viewModel.HasEmptyState);
    }

    [Fact]
    public async Task CloseAllConnectionsAsync_DisablesCloseActionsUntilTheOperationCompletes()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeConnectionClient client = new() { CloseAllResult = release.Task };
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(), client, new FakeConnectionLog(), new TestApplicationErrorSink());
        await viewModel.RefreshConnectionsAsync(CancellationToken.None);
        Task close = viewModel.CloseAllConnectionsAsync(CancellationToken.None);
        Assert.True(viewModel.IsClosing);
        Assert.False(viewModel.CanCloseConnections);

        release.SetResult();
        await close;
        Assert.False(viewModel.IsClosing);
        Assert.True(viewModel.CanCloseConnections);
    }

    [Fact]
    public async Task RefreshConnectionsAsync_OutOfOrderSuccessKeepsTheNewestSnapshot()
    {
        TaskCompletionSource<IReadOnlyList<ActiveConnection>> stale = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeConnectionClient client = new();
        client.RefreshResults.Enqueue(stale.Task);
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(), client, new FakeConnectionLog(), new TestApplicationErrorSink());
        Task first = viewModel.RefreshConnectionsAsync(CancellationToken.None);
        Assert.True(viewModel.IsRefreshing);
        await viewModel.RefreshConnectionsAsync(CancellationToken.None);
        Assert.True(viewModel.IsRefreshing);

        stale.SetResult([]);
        await first;

        Assert.False(viewModel.IsRefreshing);
        Assert.Equal(client.Connections.Count, viewModel.Connections.Count);
        Assert.Equal("2 active", viewModel.ConnectionStatusText);
    }

    [Fact]
    public async Task RefreshConnectionsAsync_OutOfOrderFailureDoesNotClearOrLogOverNewSuccess()
    {
        TaskCompletionSource<IReadOnlyList<ActiveConnection>> stale = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeConnectionClient client = new();
        FakeConnectionLog log = new();
        client.RefreshResults.Enqueue(stale.Task);
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(), client, log, new TestApplicationErrorSink());
        Task first = viewModel.RefreshConnectionsAsync(CancellationToken.None);
        await viewModel.RefreshConnectionsAsync(CancellationToken.None);

        stale.SetException(new HttpRequestException("Old request failed."));
        await first;

        Assert.Equal(client.Connections.Count, viewModel.Connections.Count);
        Assert.Equal("2 active", viewModel.ConnectionStatusText);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task RefreshConnectionsAsync_LateResponseCannotReplaceALiveStreamObservation()
    {
        TaskCompletionSource<IReadOnlyList<ActiveConnection>> stale = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeConnectionClient client = new()
        {
            StreamConnections = [new("live", "stream", "host", "rule", "", "proxy", 1, 2, DateTimeOffset.UnixEpoch)],
        };
        client.RefreshResults.Enqueue(stale.Task);
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(), client, new FakeConnectionLog(), new TestApplicationErrorSink());
        using CancellationTokenSource lifetime = new();
        Task first = viewModel.RefreshConnectionsAsync(CancellationToken.None);
        Task stream = viewModel.WatchConnectionsAsync(lifetime.Token);
        try
        {
            stale.SetResult(client.Connections);
            await first;
            Assert.Equal("live", Assert.Single(viewModel.Connections).Connection.Id);
        }
        finally
        {
            lifetime.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
        }
    }

    /// <summary>Verifies construction loads labels and initial status text.</summary>
    [Fact]
    public void Constructor_LoadsLabelsAndInitialStatus()
    {
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            new FakeConnectionClient(),
            new FakeConnectionLog(),
            new TestApplicationErrorSink());

        Assert.Equal("Connections", viewModel.PageTitleText);
        Assert.Equal("Description", viewModel.DescriptionText);
        Assert.Equal("Refresh", viewModel.RefreshConnectionsText);
        Assert.Equal("Close all", viewModel.CloseAllConnectionsText);
        Assert.Equal("Close", viewModel.CloseConnectionText);
        Assert.Equal("Not refreshed", viewModel.ConnectionStatusText);
    }

    /// <summary>Verifies refresh success loads rows and updates status.</summary>
    [Fact]
    public async Task RefreshConnectionsAsync_WhenSuccessful_LoadsRows()
    {
        FakeConnectionClient client = new();
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            client,
            new FakeConnectionLog(),
            new TestApplicationErrorSink(),
            text => $"display:{text}");

        await viewModel.RefreshConnectionsAsync(CancellationToken.None);

        Assert.Equal(client.Connections.Count, viewModel.Connections.Count);
        Assert.Equal(client.Connections[0], viewModel.Connections[0].Connection);
        Assert.Equal("display:proc", viewModel.Connections[0].ProcessNameDisplay);
        Assert.Equal("display:host", viewModel.Connections[0].HostDisplay);
        Assert.Equal("display:rule,payload", viewModel.Connections[0].RuleDisplay);
        Assert.Equal("display:proxy", viewModel.Connections[0].ProxyNameDisplay);
        Assert.Equal("10.0 B", viewModel.Connections[0].UploadDisplay);
        Assert.Equal("20.0 B", viewModel.Connections[0].DownloadDisplay);
        Assert.Equal("2 active", viewModel.ConnectionStatusText);
    }

    /// <summary>Verifies refresh failures clear rows and log a warning.</summary>
    [Fact]
    public async Task RefreshConnectionsAsync_WhenFailure_ClearsRowsAndLogs()
    {
        FakeConnectionClient client = new() { ExceptionToThrow = new HttpRequestException("offline") };
        FakeConnectionLog log = new();
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            client,
            log,
            new TestApplicationErrorSink());

        await viewModel.RefreshConnectionsAsync(CancellationToken.None);

        Assert.Empty(viewModel.Connections);
        Assert.Equal("Unavailable", viewModel.ConnectionStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Warning" && entry.Detail == "offline");
    }

    /// <summary>Verifies a canceled stale response cannot overwrite page state after navigation or a newer action.</summary>
    [Fact]
    public async Task RefreshConnectionsAsync_WhenCancellationWins_DoesNotPublishStaleRows()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            new FakeConnectionClient(),
            new FakeConnectionLog(),
            new TestApplicationErrorSink());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.RefreshConnectionsAsync(cancellation.Token));

        Assert.Empty(viewModel.Connections);
        Assert.Equal("Not refreshed", viewModel.ConnectionStatusText);
    }

    /// <summary>Verifies live snapshots replace the visible REST snapshot until page cancellation.</summary>
    [Fact]
    public async Task WatchConnectionsAsync_PublishesStreamSnapshotUntilCancelled()
    {
        FakeConnectionClient client = new()
        {
            StreamConnections =
            [
                new("live", "stream", "host", "rule", "", "proxy", 1, 2, DateTimeOffset.UnixEpoch),
            ],
        };
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            client,
            new FakeConnectionLog(),
            new TestApplicationErrorSink());
        TaskCompletionSource applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ConnectionsViewModel.Connections)
                && viewModel.Connections.Count == 1
                && viewModel.Connections[0].Connection.Id == "live")
            {
                applied.TrySetResult();
            }
        };
        using CancellationTokenSource cancellation = new();

        Task watch = viewModel.WatchConnectionsAsync(cancellation.Token);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
        Assert.Equal("1 active", viewModel.ConnectionStatusText);
    }

    /// <summary>Verifies closing one connection calls mihomo and refreshes the visible list.</summary>
    [Fact]
    public async Task CloseConnectionAsync_WhenSuccessful_ClosesConnectionAndRefreshes()
    {
        FakeConnectionClient client = new();
        FakeConnectionLog log = new();
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            client,
            log,
            new TestApplicationErrorSink());

        await viewModel.CloseConnectionAsync(client.Connections[0], CancellationToken.None);

        Assert.Equal("1", client.ClosedConnectionId);
        Assert.Equal(1, client.RefreshCount);
        Assert.Equal("Closed", viewModel.ConnectionStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Message == "Closed" && entry.Detail == "1");
    }

    /// <summary>Verifies a failed post-close refresh remains visible instead of being overwritten by success text.</summary>
    [Fact]
    public async Task CloseConnectionAsync_WhenRefreshFails_PreservesUnavailableStatus()
    {
        FakeConnectionClient client = new() { ExceptionToThrow = new HttpRequestException("offline") };
        FakeConnectionLog log = new();
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            client,
            log,
            new TestApplicationErrorSink());

        await viewModel.CloseConnectionAsync(client.Connections[0], CancellationToken.None);

        Assert.Equal("1", client.ClosedConnectionId);
        Assert.Equal("Unavailable", viewModel.ConnectionStatusText);
        Assert.DoesNotContain(log.Entries, entry => entry.Message == "Closed");
        Assert.Contains(log.Entries, entry => entry.Level == "Warning" && entry.Detail == "offline");
    }

    /// <summary>Verifies closing all connections calls mihomo and refreshes the visible list.</summary>
    [Fact]
    public async Task CloseAllConnectionsAsync_WhenSuccessful_ClosesAllConnectionsAndRefreshes()
    {
        FakeConnectionClient client = new();
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            client,
            new FakeConnectionLog(),
            new TestApplicationErrorSink());

        await viewModel.CloseAllConnectionsAsync(CancellationToken.None);

        Assert.True(client.CloseAllCalled);
        Assert.Equal(1, client.RefreshCount);
        Assert.Equal("Closed all", viewModel.ConnectionStatusText);
    }

    /// <summary>Verifies a failed refresh after close-all is not masked by a close-all success status.</summary>
    [Fact]
    public async Task CloseAllConnectionsAsync_WhenRefreshFails_PreservesUnavailableStatus()
    {
        FakeConnectionClient client = new() { ExceptionToThrow = new HttpRequestException("offline") };
        ConnectionsViewModel viewModel = new(
            new FakeConnectionsLocalization(),
            client,
            new FakeConnectionLog(),
            new TestApplicationErrorSink());

        await viewModel.CloseAllConnectionsAsync(CancellationToken.None);

        Assert.True(client.CloseAllCalled);
        Assert.Equal("Unavailable", viewModel.ConnectionStatusText);
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
                "Command.CloseAll" => "Close all",
                "Command.Close" => "Close",
                "Connections.Status.NotRefreshed" => "Not refreshed",
                "Connections.Status.Active.Format" => "{0} active",
                "Connections.Status.Unavailable" => "Unavailable",
                "Connections.Status.Closed" => "Closed",
                "Connections.Status.ClosedAll" => "Closed all",
                "Connections.Empty" => "No active connections",
                "Connections.NoMatches" => "No matches",
                "Connections.FilterCount.Format" => "{0} of {1}",
                _ => key,
            };
        }
    }

    /// <summary>Fake active connection client for connection tests.</summary>
    private sealed class FakeConnectionClient : IActiveConnectionClient
    {
        /// <summary>Gets fake active connections.</summary>
        /// <value>Configured active connections.</value>
        public IReadOnlyList<ActiveConnection> Connections { get; init; } =
        [
            new("1", "proc", "host", "rule", "payload", "proxy", 10, 20, DateTimeOffset.UnixEpoch),
            new("2", "proc", "host", "rule", "payload", "proxy", 30, 40, DateTimeOffset.UnixEpoch),
        ];

        /// <summary>Gets or sets exception thrown on refresh.</summary>
        /// <value>Exception thrown when non-null.</value>
        public Exception? ExceptionToThrow { get; set; }

        public IReadOnlyList<ActiveConnection>? StreamConnections { get; set; }

        /// <summary>Gets the number of refresh calls.</summary>
        /// <value>Refresh call count.</value>
        public int RefreshCount { get; private set; }

        public Queue<Task<IReadOnlyList<ActiveConnection>>> RefreshResults { get; } = new();

        /// <summary>Gets the last closed connection id.</summary>
        /// <value>Closed connection id, or null when none was closed.</value>
        public string? ClosedConnectionId { get; private set; }

        /// <summary>Gets whether close-all was called.</summary>
        /// <value>True when close-all was called.</value>
        public bool CloseAllCalled { get; private set; }

        public Task CloseAllResult { get; init; } = Task.CompletedTask;

        /// <summary>Gets fake active connections.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured active connections.</returns>
        public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            if (RefreshResults.TryDequeue(out Task<IReadOnlyList<ActiveConnection>>? result))
            {
                return result;
            }

            return ExceptionToThrow is null
                ? Task.FromResult(Connections)
                : Task.FromException<IReadOnlyList<ActiveConnection>>(ExceptionToThrow);
        }

        public async IAsyncEnumerable<IReadOnlyList<ActiveConnection>> StreamActiveConnectionsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return StreamConnections ?? Connections;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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
            return CloseAllResult;
        }
    }

    /// <summary>Fake connection log for connection tests.</summary>
    private sealed class FakeConnectionLog : IConnectionLog
    {
        /// <summary>Gets captured log entries.</summary>
        /// <value>Mutable captured log entries.</value>
        public List<LogEntry> Entries { get; } = [];

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
