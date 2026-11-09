using System.Globalization;
using System.Net.Http;
using ClashSharp.Model;
using ClashSharp.Tests.Unit.ViewModel;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Contract of the connections page model: whatever mix of refreshes, closes, filters, failures,
/// observation resets and language changes a user produces, the visible list, counters, status and
/// commands stay consistent with the last observation and no command stays disabled.
/// </summary>
public sealed class ConnectionsViewModelContractTests
{
    private enum Operation
    {
        RefreshCommand,
        RefreshAfterServerChange,
        RefreshFailing,
        RefreshLimited,
        CloseOne,
        CloseOneMissing,
        CloseOneFailing,
        CloseAll,
        CloseAllFailing,
        SetSearch,
        ChangeLanguage,
        ResetObservation,
    }

    private enum Observation
    {
        None,
        Rows,
        Unavailable,
        Omitted,
    }

    /// <summary>Gets one row per seeded schedule.</summary>
    public static TheoryData<int> Seeds
    {
        get
        {
            TheoryData<int> data = [];
            for (int seed = 0; seed < 10; seed++)
            {
                data.Add(seed);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task RandomUserSessions_KeepListFilterCountersStatusAndCommandsConsistent(int seed)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Random random = new(20261109 + seed);
        MutablePageLocalization localization = new() { Language = ViewModelContractSupport.AllLanguages[seed % ViewModelContractSupport.AllLanguages.Length] };
        ScriptedConnectionClient client = new() { Server = RandomServer(random, 0) };
        TestApplicationErrorSink errors = new();
        ConnectionsViewModel page = new(localization, client, new NullLog(), errors);
        Observation observation = Observation.None;
        int omitted = 0;
        string statusKey = "Connections.Status.NotRefreshed";
        object[] statusArguments = [];
        Operation[] operations = Enum.GetValues<Operation>();
        string[] searches = ["", "a", "EXAMPLE", "中", "  host-1  ", new string('x', 300), "zzz-no-match", "unknown"];

        for (int step = 0; step < 150; step++)
        {
            Operation operation = operations[random.Next(operations.Length)];
            string label = $"seed {seed} step {step} {operation}";
            switch (operation)
            {
                case Operation.RefreshCommand:
                    await RunAsync(page.RefreshConnectionsCommand, null);
                    observation = Observation.Rows;
                    statusKey = "Connections.Status.Active.Format";
                    statusArguments = [client.Server.Count];
                    break;
                case Operation.RefreshAfterServerChange:
                    client.Server = RandomServer(random, step);
                    await RunAsync(page.RefreshConnectionsCommand, null);
                    observation = Observation.Rows;
                    statusKey = "Connections.Status.Active.Format";
                    statusArguments = [client.Server.Count];
                    break;
                case Operation.RefreshFailing:
                    client.FailNextRead = true;
                    await RunAsync(page.RefreshConnectionsCommand, null);
                    observation = Observation.Unavailable;
                    statusKey = "Connections.Status.Unavailable";
                    statusArguments = [];
                    break;
                case Operation.RefreshLimited:
                    client.LimitNextReadTo = 5000 + random.Next(100);
                    omitted = client.LimitNextReadTo;
                    await RunAsync(page.RefreshConnectionsCommand, null);
                    observation = Observation.Omitted;
                    statusKey = "Connections.Status.DetailsOmitted";
                    statusArguments = [];
                    break;
                case Operation.CloseOne:
                case Operation.CloseOneMissing:
                case Operation.CloseOneFailing:
                    {
                        ActiveConnectionDisplayRow? row = page.Connections.Count == 0
                            ? null
                            : page.Connections[random.Next(page.Connections.Count)];
                        if (operation == Operation.CloseOneMissing || row is null)
                        {
                            // A row that vanished server-side: the close is a no-op upstream and the list refreshes.
                            row = new ActiveConnectionDisplayRow(
                                new ActiveConnection("missing-" + step, "p", "h", "r", "", "x", 0, 0, DateTimeOffset.UnixEpoch),
                                text => text);
                        }

                        client.FailNextClose = operation == Operation.CloseOneFailing;
                        await RunAsync(page.CloseConnectionCommand, row);
                        if (client.FailNextClose || operation == Operation.CloseOneFailing)
                        {
                            observation = Observation.Unavailable;
                            statusKey = "Connections.Status.Unavailable";
                            statusArguments = [];
                        }
                        else
                        {
                            observation = Observation.Rows;
                            statusKey = "Connections.Status.Closed";
                            statusArguments = [];
                        }

                        client.FailNextClose = false;
                        break;
                    }

                case Operation.CloseAll:
                case Operation.CloseAllFailing:
                    client.FailNextClose = operation == Operation.CloseAllFailing;
                    await RunAsync(page.CloseAllConnectionsCommand, null);
                    if (operation == Operation.CloseAllFailing)
                    {
                        observation = Observation.Unavailable;
                        statusKey = "Connections.Status.Unavailable";
                        statusArguments = [];
                    }
                    else
                    {
                        observation = Observation.Rows;
                        statusKey = "Connections.Status.ClosedAll";
                        statusArguments = [];
                    }

                    client.FailNextClose = false;
                    break;
                case Operation.SetSearch:
                    page.SearchText = searches[random.Next(searches.Length)];
                    break;
                case Operation.ChangeLanguage:
                    localization.Language = ViewModelContractSupport.AllLanguages[random.Next(ViewModelContractSupport.AllLanguages.Length)];
                    page.RefreshLocalization();
                    break;
                default:
                    page.ResetDataObservation();
                    // Reset forgets the observation: the page is "not refreshed" again, status says unavailable.
                    observation = Observation.None;
                    statusKey = "Connections.Status.Unavailable";
                    statusArguments = [];
                    break;
            }

            AssertConsistent(page, client, localization, observation, omitted, statusKey, statusArguments, label);
        }

        Assert.Empty(errors.Errors);
    }

    [Fact]
    public async Task ALateAnswerFromASupersededRefresh_NeverOverwritesANewerObservation()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        TaskCompletionSource<IReadOnlyList<ActiveConnection>> slow = new();
        Queue<Func<Task<IReadOnlyList<ActiveConnection>>>> reads = new();
        reads.Enqueue(() => slow.Task);
        reads.Enqueue(() => Task.FromResult<IReadOnlyList<ActiveConnection>>([Row("new-1"), Row("new-2")]));
        ScriptedConnectionClient client = new() { Reads = reads };
        ConnectionsViewModel page = new(localization, client, new NullLog(), new TestApplicationErrorSink());

        Task<IReadOnlyList<ActiveConnection>> first = page.RefreshConnectionsAsync(CancellationToken.None);
        IReadOnlyList<ActiveConnection> second = await page.RefreshConnectionsAsync(CancellationToken.None);
        slow.SetResult([Row("old-1")]);
        IReadOnlyList<ActiveConnection> firstResult = await first;

        Assert.Equal(2, second.Count);
        Assert.Empty(firstResult);
        Assert.Equal(["new-1", "new-2"], page.Connections.Select(row => row.Connection.Id).ToArray());
        Assert.False(page.IsRefreshing);
    }

    [Fact]
    public async Task ResettingTheObservationDuringARefresh_DiscardsThatRefreshesResult()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        TaskCompletionSource<IReadOnlyList<ActiveConnection>> gate = new();
        ScriptedConnectionClient client = new() { Reads = new Queue<Func<Task<IReadOnlyList<ActiveConnection>>>>([() => gate.Task]) };
        ConnectionsViewModel page = new(localization, client, new NullLog(), new TestApplicationErrorSink());
        Task<IReadOnlyList<ActiveConnection>> refresh = page.RefreshConnectionsAsync(CancellationToken.None);
        Assert.True(page.IsRefreshing);

        page.ResetDataObservation();
        gate.SetResult([Row("stale-from-old-data-directory")]);
        await refresh;

        Assert.Empty(page.Connections);
        Assert.False(page.IsRefreshing);
        Assert.Equal(localization.GetString("Connections.Status.Unavailable"), page.ConnectionStatusText);
    }

    [Fact]
    public async Task CancelledRefreshAndClose_LeaveNoBusyFlagAndNoReportedError()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        TestApplicationErrorSink errors = new();
        ScriptedConnectionClient client = new() { BlockReadsUntilCancelled = true, BlockClosesUntilCancelled = true };
        ConnectionsViewModel page = new(localization, client, new NullLog(), errors);
        using CancellationTokenSource cancellation = new();

        Task refresh = page.RefreshConnectionsCommand.ExecuteObservedAsync(null, cancellation.Token);
        Assert.True(page.IsRefreshing);
        cancellation.Cancel();
        await refresh;

        using CancellationTokenSource closing = new();
        ActiveConnectionDisplayRow row = new(Row("c"), text => text);
        Task close = page.CloseConnectionCommand.ExecuteObservedAsync(row, closing.Token);
        Assert.True(page.IsClosing);
        closing.Cancel();
        await close;
        using CancellationTokenSource closingAll = new();
        Task closeAll = page.CloseAllConnectionsCommand.ExecuteObservedAsync(null, closingAll.Token);
        Assert.True(page.IsClosing);
        closingAll.Cancel();
        await closeAll;

        Assert.False(page.IsRefreshing);
        Assert.False(page.IsClosing);
        Assert.Empty(errors.Errors);
        Assert.All(
            [page.RefreshConnectionsCommand, page.CloseConnectionCommand, page.CloseAllConnectionsCommand],
            command => Assert.True(command.CanExecute(null)));
    }

    [Fact]
    public async Task LargeSnapshots_AreFilteredCorrectlyAndKeepTheirOrder()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        List<ActiveConnection> server = [.. Enumerable.Range(0, 30_000).Select(index => new ActiveConnection(
            "id-" + index.ToString(CultureInfo.InvariantCulture),
            index % 7 == 0 ? "unknown" : "proc-" + (index % 13),
            "host-" + index + ".example",
            index % 5 == 0 ? "DOMAIN-SUFFIX" : "MATCH",
            "",
            index % 3 == 0 ? "ProxyA" : "ProxyB",
            index,
            index * 2L,
            DateTimeOffset.UnixEpoch.AddSeconds(index)))];
        ScriptedConnectionClient client = new() { Server = server };
        ConnectionsViewModel page = new(localization, client, new NullLog(), new TestApplicationErrorSink());

        await RunAsync(page.RefreshConnectionsCommand, null);
        Assert.Equal(server.Count, page.Connections.Count);

        page.SearchText = "HOST-2999";
        string[] expected = [.. server.Where(row => row.Host.Contains("host-2999", StringComparison.OrdinalIgnoreCase)).Select(row => row.Id)];
        Assert.Equal(expected, page.Connections.Select(row => row.Connection.Id).ToArray());
        Assert.NotEmpty(expected);

        page.SearchText = "proxyb";
        Assert.Equal(
            server.Where(row => row.ProxyName == "ProxyB").Select(row => row.Id).ToArray(),
            page.Connections.Select(row => row.Connection.Id).ToArray());

        page.SearchText = string.Empty;
        Assert.Equal(server.Count, page.Connections.Count);
        Assert.True(page.CanCloseConnections);
    }

    [Fact]
    public void SearchText_IsTruncatedToTheDocumentedBound_AndNullBecomesEmpty()
    {
        ConnectionsViewModel page = new(new MutablePageLocalization(), new ScriptedConnectionClient(), new NullLog(), new TestApplicationErrorSink());

        page.SearchText = new string('q', 1000);
        Assert.Equal(256, page.SearchText.Length);

        page.SearchText = null!;
        Assert.Equal(string.Empty, page.SearchText);
    }

    [Fact]
    public async Task Filter_AcceptsExactlyTheRowsWhoseVisibleFieldsContainTheQuery_ForGeneratedQueries()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Random random = new(20261109);
        MutablePageLocalization localization = new();
        ScriptedConnectionClient client = new() { Server = RandomServer(random, 1, 80) };
        ConnectionsViewModel page = new(localization, client, new NullLog(), new TestApplicationErrorSink());
        await RunAsync(page.RefreshConnectionsCommand, null);
        ActiveConnectionDisplayRow[] all = [.. page.Connections];

        for (int round = 0; round < 300; round++)
        {
            string query = RandomQuery(random, all);
            page.SearchText = query;
            string trimmed = page.SearchText.Trim();

            string[] expected = [.. all
                .Where(row => trimmed.Length == 0
                    || row.ProcessNameDisplay.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                    || row.HostDisplay.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                    || row.RuleDisplay.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                    || row.ProxyNameDisplay.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.Connection.Id)];
            Assert.True(
                expected.SequenceEqual(page.Connections.Select(row => row.Connection.Id)),
                $"Filter '{query}' disagrees with the field-containment contract.");
            Assert.Equal(page.Connections.Count == 0, page.HasEmptyState);
        }
    }

    /// <summary>Gets one row per language pair source.</summary>
    public static TheoryData<AppLanguage> Languages
    {
        get
        {
            TheoryData<AppLanguage> data = [];
            foreach (AppLanguage language in ViewModelContractSupport.AllLanguages)
            {
                data.Add(language);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task SwitchingFromAnyLanguage_ShowsExactlyWhatAFreshPageShows_ForEveryOtherLanguage(AppLanguage from)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        foreach (AppLanguage to in ViewModelContractSupport.LanguagePairs(from))
        {
            foreach (Observation state in new[] { Observation.None, Observation.Rows, Observation.Unavailable, Observation.Omitted })
            {
                (ConnectionsViewModel switched, MutablePageLocalization switchedLocalization) = await BuildAsync(from, state);
                (ConnectionsViewModel fresh, _) = await BuildAsync(to, state);

                ViewModelContractSupport.AssertSwitchEqualsFreshAndAnnouncesEveryChange(
                    $"{from}->{to} {state}",
                    switched,
                    fresh,
                    () =>
                    {
                        switchedLocalization.Language = to;
                        switched.RefreshLocalization();
                    });

                if (state == Observation.Rows)
                {
                    Assert.Equal(
                        fresh.Connections.Select(row => (row.ProcessNameDisplay, row.UploadDisplay, row.DownloadDisplay)).ToArray(),
                        switched.Connections.Select(row => (row.ProcessNameDisplay, row.UploadDisplay, row.DownloadDisplay)).ToArray());
                }
            }
        }
    }

    [Fact]
    public async Task WatchingTheStream_AppliesSnapshotsAndStopsQuietlyOnCancellation()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        StreamingConnectionClient client = new();
        TestApplicationErrorSink errors = new();
        ConnectionsViewModel page = new(localization, client, new NullLog(), errors);
        using CancellationTokenSource cancellation = new();

        Task watch = page.WatchConnectionsAsync(cancellation.Token);
        client.Publish([Row("s1"), Row("s2")]);
        await client.WaitForConsumedAsync(1);
        Assert.Equal(["s1", "s2"], page.Connections.Select(row => row.Connection.Id).ToArray());
        client.Publish([Row("s3")]);
        await client.WaitForConsumedAsync(2);
        Assert.Equal(["s3"], page.Connections.Select(row => row.Connection.Id).ToArray());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
        Assert.Empty(errors.Errors);
    }

    private static void AssertConsistent(
        ConnectionsViewModel page,
        ScriptedConnectionClient client,
        MutablePageLocalization localization,
        Observation observation,
        int omitted,
        string statusKey,
        object[] statusArguments,
        string label)
    {
        // Nothing is left running and no command is disabled between user actions.
        Assert.False(page.IsRefreshing, label);
        Assert.False(page.IsClosing, label);
        Assert.True(page.RefreshConnectionsCommand.CanExecute(null), label);
        Assert.True(page.CloseConnectionCommand.CanExecute(null), label);
        Assert.True(page.CloseAllConnectionsCommand.CanExecute(null), label);

        string query = page.SearchText.Trim();
        ActiveConnection[] known = observation == Observation.Rows ? [.. client.LastDelivered] : [];
        ActiveConnectionDisplayRow[] model = [.. known.Select(connection =>
            new ActiveConnectionDisplayRow(connection, text => text, ((IDisplayPageLocalization)localization).Formatter, localization.GetString))];
        string[] expectedIds = [.. model
            .Where(row => query.Length == 0
                || row.ProcessNameDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.HostDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.RuleDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.ProxyNameDisplay.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Connection.Id)];

        Assert.True(
            expectedIds.SequenceEqual(page.Connections.Select(row => row.Connection.Id)),
            $"{label}: visible ids {string.Join(",", page.Connections.Select(row => row.Connection.Id))} != expected {string.Join(",", expectedIds)}");
        Assert.Equal(page.Connections.Count == 0, page.HasEmptyState);
        Assert.Equal(!page.HasEmptyState, page.HasStatusSummary);
        int total = observation == Observation.Omitted ? omitted : known.Length;
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, localization.GetString("Connections.FilterCount.Format"), page.Connections.Count, total),
            page.FilterCountText);

        string expectedStatus = statusArguments.Length == 0
            ? localization.GetString(statusKey)
            : string.Format(CultureInfo.CurrentCulture, localization.GetString(statusKey), statusArguments);
        Assert.True(expectedStatus == page.ConnectionStatusText, $"{label}: status '{page.ConnectionStatusText}' != '{expectedStatus}'");

        switch (observation)
        {
            case Observation.None:
                Assert.Equal(localization.GetString("Connections.Status.NotRefreshed"), page.EmptyStateText);
                Assert.False(page.CanCloseConnections, label);
                break;
            case Observation.Unavailable:
                Assert.Empty(page.Connections);
                Assert.Equal(localization.GetString("Connections.Status.Unavailable"), page.EmptyStateText);
                Assert.False(page.CanCloseConnections, label);
                break;
            case Observation.Omitted:
                Assert.Empty(page.Connections);
                Assert.Equal(localization.GetString("Connections.Status.DetailsOmitted"), page.EmptyStateText);
                Assert.True(page.CanCloseConnections, label);
                break;
            default:
                Assert.Equal(known.Length > 0, page.CanCloseConnections);
                if (known.Length == 0)
                {
                    Assert.Equal(localization.GetString("Connections.Empty"), page.EmptyStateText);
                }

                break;
        }

        Assert.Equal(localization.GetString("Nav.Connections"), page.PageTitleText);
        Assert.Equal(localization.GetString("Command.CloseAll"), page.CloseAllConnectionsText);
        foreach (ActiveConnectionDisplayRow row in page.Connections)
        {
            ActiveConnectionDisplayRow reference = model.Single(candidate => candidate.Connection.Id == row.Connection.Id);
            Assert.Equal(reference.ProcessNameDisplay, row.ProcessNameDisplay);
        }
    }

    private static async Task RunAsync(AsyncRelayCommand command, object? parameter)
    {
        await command.ExecuteObservedAsync(parameter, CancellationToken.None);
    }

    private static ActiveConnection Row(string id) =>
        new(id, "proc", "host.example", "MATCH", string.Empty, "DIRECT", 1, 2, DateTimeOffset.UnixEpoch);

    private static List<ActiveConnection> RandomServer(Random random, int salt, int maximum = 25)
    {
        int count = random.Next(0, maximum + 1);
        string[] processes = ["chrome.exe", "unknown", "", "curl", "中文进程", "x y"];
        string[] hosts = ["example.com", "host-1.test", "EXAMPLE.org", "a.b.c", "例子.测试", "10.0.0.1", string.Empty];
        return [.. Enumerable.Range(0, count).Select(index => new ActiveConnection(
            $"c{salt}-{index}",
            processes[random.Next(processes.Length)],
            hosts[random.Next(hosts.Length)],
            random.Next(2) == 0 ? "MATCH" : "DOMAIN-SUFFIX",
            random.Next(2) == 0 ? string.Empty : "ads",
            random.Next(3) == 0 ? "DIRECT" : "Proxy / Node " + random.Next(4),
            random.NextInt64(0, 5_000_000_000),
            random.NextInt64(0, 5_000_000_000),
            DateTimeOffset.UnixEpoch.AddSeconds(random.Next(0, 1_000_000))))];
    }

    private static string RandomQuery(Random random, ActiveConnectionDisplayRow[] rows)
    {
        if (rows.Length == 0 || random.Next(4) == 0)
        {
            return new string(Enumerable.Range(0, random.Next(0, 4)).Select(_ => "ab. 中xz"[random.Next(7)]).ToArray());
        }

        ActiveConnectionDisplayRow row = rows[random.Next(rows.Length)];
        string source = random.Next(4) switch
        {
            0 => row.HostDisplay,
            1 => row.ProcessNameDisplay,
            2 => row.RuleDisplay,
            _ => row.ProxyNameDisplay,
        };
        if (source.Length == 0)
        {
            return "~";
        }

        int start = random.Next(source.Length);
        string slice = source.Substring(start, random.Next(1, source.Length - start + 1));
        return random.Next(3) == 0 ? slice.ToUpperInvariant() : slice;
    }

    private static async Task<(ConnectionsViewModel Page, MutablePageLocalization Localization)> BuildAsync(
        AppLanguage language,
        Observation state)
    {
        MutablePageLocalization localization = new() { Language = language };
        ScriptedConnectionClient client = new()
        {
            Server = [Row("one"), new ActiveConnection("two", "unknown", "second.example", "DOMAIN", "x", "Node", 1536, 3_145_728, DateTimeOffset.UnixEpoch)],
        };
        ConnectionsViewModel page = new(localization, client, new NullLog(), new TestApplicationErrorSink());
        switch (state)
        {
            case Observation.Rows:
                await page.RefreshConnectionsAsync(CancellationToken.None);
                break;
            case Observation.Unavailable:
                client.FailNextRead = true;
                await page.RefreshConnectionsAsync(CancellationToken.None);
                break;
            case Observation.Omitted:
                client.LimitNextReadTo = 9000;
                await page.RefreshConnectionsAsync(CancellationToken.None);
                break;
            default:
                break;
        }

        return (page, localization);
    }

    private sealed class NullLog : IConnectionLog
    {
        public void Append(string level, string category, string message, string? detail)
        {
        }
    }

    private sealed class ScriptedConnectionClient : IActiveConnectionClient
    {
        internal List<ActiveConnection> Server { get; set; } = [];

        internal IReadOnlyList<ActiveConnection> LastDelivered { get; private set; } = [];

        internal bool FailNextRead { get; set; }

        internal bool FailNextClose { get; set; }

        internal int LimitNextReadTo { get; set; }

        internal bool BlockReadsUntilCancelled { get; set; }

        internal bool BlockClosesUntilCancelled { get; set; }

        internal Queue<Func<Task<IReadOnlyList<ActiveConnection>>>>? Reads { get; set; }

        public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
        {
            if (Reads is { Count: > 0 })
            {
                IReadOnlyList<ActiveConnection> scripted = await Reads.Dequeue()();
                LastDelivered = scripted;
                return scripted;
            }

            if (BlockReadsUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (FailNextRead)
            {
                FailNextRead = false;
                throw new HttpRequestException("controller unavailable");
            }

            if (LimitNextReadTo > 0)
            {
                int total = LimitNextReadTo;
                LimitNextReadTo = 0;
                throw new IncompleteConnectionSnapshotException(total);
            }

            LastDelivered = [.. Server];
            return LastDelivered;
        }

        public IAsyncEnumerable<IReadOnlyList<ActiveConnection>> StreamActiveConnectionsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken)
        {
            if (BlockClosesUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (FailNextClose)
            {
                throw new HttpRequestException("close failed");
            }

            Server.RemoveAll(connection => connection.Id == connectionId);
            await Task.CompletedTask;
        }

        public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
        {
            if (BlockClosesUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (FailNextClose)
            {
                throw new HttpRequestException("close-all failed");
            }

            Server.Clear();
            await Task.CompletedTask;
        }
    }

    private sealed class StreamingConnectionClient : IActiveConnectionClient
    {
        private readonly System.Threading.Channels.Channel<IReadOnlyList<ActiveConnection>> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyList<ActiveConnection>>();
        private readonly SemaphoreSlim _consumed = new(0);

        internal void Publish(IReadOnlyList<ActiveConnection> snapshot) => _channel.Writer.TryWrite(snapshot);

        internal async Task WaitForConsumedAsync(int count)
        {
            for (int index = 0; index < count; index++)
            {
                await _consumed.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<IReadOnlyList<ActiveConnection>> StreamActiveConnectionsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                IReadOnlyList<ActiveConnection> next = await _channel.Reader.ReadAsync(cancellationToken);
                yield return next;
                _consumed.Release();
            }
        }

        public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAllConnectionsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
