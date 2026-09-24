using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the proxies view model.</summary>
public sealed class ProxiesViewModelTests
{
    /// <summary>Verifies construction initializes labels without reading the proxy catalog.</summary>
    [Fact]
    public void Constructor_IsSideEffectFree()
    {
        FakeProxyCatalog catalog = new();

        ProxiesViewModel viewModel = new(
            new FakeProxiesLocalization(),
            catalog,
            new FakeProxyLatency(),
            new FakeProxiesLog(),
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));

        Assert.Equal("Proxy nodes", viewModel.PageTitleText);
        Assert.Equal("Refresh", viewModel.RefreshNodesText);
        Assert.Equal("Refresh runtime", viewModel.RefreshRuntimeText);
        Assert.Equal("Test latency", viewModel.TestLatencyText);
        Assert.Equal("Strategy groups", viewModel.ProxyGroupsSectionTitleText);
        Assert.Equal("Resources", viewModel.ProviderResourcesSectionTitleText);
        Assert.Empty(viewModel.ProxyNodes);
        Assert.Equal(0, catalog.ReadCount);
    }

    /// <summary>Verifies explicit loading replaces visible nodes from the catalog.</summary>
    [Fact]
    public async Task LoadAsync_LoadsCatalogAndRuntimeState()
    {
        FakeProxyCatalog catalog = new();
        FakeProxyRuntimeController runtime = new();
        ProxiesViewModel viewModel = new(
            new FakeProxiesLocalization(),
            catalog,
            new FakeProxyLatency(),
            runtime,
            new FakeProxiesLog(),
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => $"display:{text}"));
        IReadOnlyList<ProxyNode> updatedNodes =
        [
            new("Updated", "HTTPS", new RegionMetadata("US", "United States", "us"), null),
        ];
        catalog.Nodes = updatedNodes;

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(updatedNodes, viewModel.ProxyNodes.Select(static row => row.Model));
        Assert.Equal(runtime.ProxyGroups, viewModel.ProxyGroups.Select(static row => row.Model));
        Assert.Equal(runtime.ProviderResources, viewModel.ProviderResources.Select(static row => row.Model));
        Assert.Equal("display:Updated", viewModel.ProxyNodes[0].NameDisplay);
        Assert.Equal(1, catalog.ReadCount);
    }

    /// <summary>Verifies latency testing updates visible nodes and logs success.</summary>
    [Fact]
    public async Task TestLatencyAsync_WhenSuccessful_UpdatesNodesAndLogs()
    {
        FakeProxyLatency latency = new();
        FakeProxiesLog log = new();
        ProxiesViewModel viewModel = new(
            new FakeProxiesLocalization(),
            new FakeProxyCatalog(),
            latency,
            log,
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
        IReadOnlyList<ProxyNode> testedNodes =
        [
            new("Direct", "DIRECT", new RegionMetadata("CN", "China", "cn"), 0),
        ];
        latency.TestedNodes = testedNodes;

        await viewModel.TestLatencyAsync(CancellationToken.None);

        Assert.Equal(testedNodes, viewModel.ProxyNodes.Select(static row => row.Model));
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Category == "ProxyNodes");
    }

    /// <summary>Verifies expected latency failures are logged without replacing visible nodes.</summary>
    [Fact]
    public async Task TestLatencyAsync_WhenExpectedFailure_LogsWarning()
    {
        FakeProxyLatency latency = new()
        {
            ExceptionToThrow = new InvalidOperationException("probe failed"),
        };
        FakeProxiesLog log = new();
        ProxiesViewModel viewModel = new(
            new FakeProxiesLocalization(),
            new FakeProxyCatalog(),
            latency,
            log,
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
        IReadOnlyList<ProxyNodeDisplay> originalNodes = viewModel.ProxyNodes;

        await viewModel.TestLatencyAsync(CancellationToken.None);

        Assert.Equal(originalNodes, viewModel.ProxyNodes);
        Assert.Contains(log.Entries, entry => entry.Level == "Warning" && entry.Detail == "probe failed");
    }

    /// <summary>Verifies runtime refresh loads strategy groups and provider resources.</summary>
    [Fact]
    public async Task RefreshRuntimeAsync_LoadsProxyGroupsAndProviders()
    {
        FakeProxyRuntimeController runtime = new();
        ProxiesViewModel viewModel = new(
            new FakeProxiesLocalization(),
            new FakeProxyCatalog(),
            new FakeProxyLatency(),
            runtime,
            new FakeProxiesLog(),
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));

        await viewModel.RefreshRuntimeAsync(CancellationToken.None);

        Assert.Equal(runtime.ProxyGroups, viewModel.ProxyGroups.Select(static row => row.Model));
        Assert.Equal(runtime.ProviderResources, viewModel.ProviderResources.Select(static row => row.Model));
        Assert.Equal("Runtime refreshed", viewModel.RuntimeStatusText);
    }

    /// <summary>Verifies selecting a strategy group node writes through the runtime controller and refreshes groups.</summary>
    [Fact]
    public async Task SelectProxyAsync_UpdatesRuntimeSelectionAndRefreshes()
    {
        FakeProxyRuntimeController runtime = new();
        ProxiesViewModel viewModel = new(
            new FakeProxiesLocalization(),
            new FakeProxyCatalog(),
            new FakeProxyLatency(),
            runtime,
            new FakeProxiesLog(),
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
        MihomoProxyGroup group = runtime.ProxyGroups[0];

        await viewModel.SelectProxyAsync(group, "Node B", CancellationToken.None);

        Assert.Equal(("Proxy", "Node B"), runtime.LastSelection);
        Assert.Equal(1, runtime.RefreshCount);
        Assert.Equal("Selection applied", viewModel.RuntimeStatusText);
    }

    /// <summary>Verifies provider update writes through the runtime controller and refreshes resources.</summary>
    [Fact]
    public async Task UpdateProviderAsync_UpdatesProviderAndRefreshes()
    {
        FakeProxyRuntimeController runtime = new();
        ProxiesViewModel viewModel = new(
            new FakeProxiesLocalization(),
            new FakeProxyCatalog(),
            new FakeProxyLatency(),
            runtime,
            new FakeProxiesLog(),
            new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
        MihomoProviderResource provider = runtime.ProviderResources[0];

        await viewModel.UpdateProviderAsync(provider, CancellationToken.None);

        Assert.Equal(provider, runtime.LastUpdatedProvider);
        Assert.Equal(1, runtime.RefreshCount);
        Assert.Equal("Provider updated", viewModel.RuntimeStatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationFollowedByRefreshFailure_PreservesRowsAndDoesNotReportSuccess(bool selectProxy)
    {
        FakeProxyRuntimeController runtime = new();
        FakeProxiesLog log = new();
        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), new FakeProxyCatalog(),
            new FakeProxyLatency(), runtime, log, new TestApplicationErrorSink(), new ModelDisplayMapper(static text => text));
        await viewModel.RefreshRuntimeAsync(CancellationToken.None);
        runtime.RefreshFailure = new System.Net.Http.HttpRequestException("Controller unavailable after mutation");

        if (selectProxy)
        {
            await viewModel.SelectProxyAsync(runtime.ProxyGroups[0], "Node B", CancellationToken.None);
            Assert.Equal(("Proxy", "Node B"), runtime.LastSelection);
        }
        else
        {
            await viewModel.UpdateProviderAsync(runtime.ProviderResources[0], CancellationToken.None);
            Assert.NotNull(runtime.LastUpdatedProvider);
        }

        Assert.NotEqual("Selection applied", viewModel.RuntimeStatusText);
        Assert.NotEqual("Provider updated", viewModel.RuntimeStatusText);
        Assert.Equal(runtime.ProviderResources, viewModel.ProviderResources.Select(row => row.Model));
        Assert.Equal(runtime.ProxyGroups, viewModel.ProxyGroups.Select(row => row.Model));
        Assert.Equal("Warning", Assert.Single(log.Entries).Level);
        runtime.RefreshFailure = null;
        await viewModel.UpdateProviderAsync(runtime.ProviderResources[0], CancellationToken.None);
        Assert.Equal("Provider updated", viewModel.RuntimeStatusText);
    }

    [Theory]
    [InlineData(MihomoProviderKind.Proxy)]
    [InlineData(MihomoProviderKind.Rule)]
    public async Task UpdateProviderAsync_EmptyResultWarnsAndRetryCanRecover(MihomoProviderKind kind)
    {
        MihomoProviderResource requested = new("shared", kind, "HTTP", "classical", 2, DateTimeOffset.UnixEpoch);
        MihomoProviderResource unrelated = requested with
        {
            Kind = kind == MihomoProviderKind.Proxy ? MihomoProviderKind.Rule : MihomoProviderKind.Proxy,
        };
        FakeProxyRuntimeController runtime = new() { ProviderResources = [unrelated, requested] };
        FakeProxiesLog log = new();
        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), new FakeProxyCatalog(),
            new FakeProxyLatency(), runtime, log, new TestApplicationErrorSink(), new ModelDisplayMapper(static text => text));
        await viewModel.RefreshRuntimeAsync(CancellationToken.None);
        runtime.ProviderResources = [unrelated, requested with { ItemCount = 0 }];

        await viewModel.UpdateProviderAsync(requested, CancellationToken.None);

        Assert.Equal("Provider updated with no entries", viewModel.RuntimeStatusText);
        Assert.Equal(0, viewModel.ProviderResources.Single(row => row.Model.Kind == kind).Model.ItemCount);
        Assert.Equal("Warning", Assert.Single(log.Entries).Level);
        runtime.ProviderResources = [unrelated, requested with { ItemCount = 1 }];
        await viewModel.UpdateProviderAsync(requested, CancellationToken.None);
        Assert.Equal("Provider updated", viewModel.RuntimeStatusText);
        Assert.Equal("Info", log.Entries[1].Level);
    }

    [Fact]
    public async Task UpdateProviderAsync_MissingResultDoesNotReportSuccess()
    {
        FakeProxyRuntimeController runtime = new();
        MihomoProviderResource requested = runtime.ProviderResources[0];
        FakeProxiesLog log = new();
        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), new FakeProxyCatalog(),
            new FakeProxyLatency(), runtime, log, new TestApplicationErrorSink(), new ModelDisplayMapper(static text => text));
        await viewModel.RefreshRuntimeAsync(CancellationToken.None);
        runtime.ProviderResources = [];

        await viewModel.UpdateProviderAsync(requested, CancellationToken.None);

        Assert.Contains("[provider.update_failed]", viewModel.RuntimeStatusText, StringComparison.Ordinal);
        Assert.Equal("Warning", Assert.Single(log.Entries).Level);
    }

    [Fact]
    public async Task UpdateProviderAsync_PreservesCollectionAndRowWhilePublishingNewValues()
    {
        FakeProxyRuntimeController runtime = new();
        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), new FakeProxyCatalog(),
            new FakeProxyLatency(), runtime, new FakeProxiesLog(), new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
        await viewModel.RefreshRuntimeAsync(CancellationToken.None);
        IReadOnlyList<MihomoProviderResourceDisplay> collection = viewModel.ProviderResources;
        MihomoProviderResourceDisplay row = collection[1];
        List<string?> changes = [];
        int collectionChanges = 0;
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        ((System.Collections.Specialized.INotifyCollectionChanged)collection).CollectionChanged += (_, _) => collectionChanges++;
        MihomoProviderResource updated = row.Model with { ItemCount = 7, UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        runtime.ProviderResources = [runtime.ProviderResources[0], updated];

        await viewModel.UpdateProviderAsync(row.Model, CancellationToken.None);

        Assert.Same(collection, viewModel.ProviderResources);
        Assert.Same(row, viewModel.ProviderResources[1]);
        Assert.Equal(updated, row.Model);
        Assert.Equal("7", row.ItemCountDisplay);
        Assert.Contains(nameof(row.ItemCountDisplay), changes);
        Assert.Contains(nameof(row.UpdatedAtDisplay), changes);
        Assert.Equal(0, collectionChanges);
    }

    [Fact]
    public async Task RefreshRuntimeAsync_ReconcilesOrderAndRemovalWithoutConfusingProviderIdentities()
    {
        MihomoProviderResource proxy = new("shared", MihomoProviderKind.Proxy, "HTTP", "", 2, null);
        MihomoProviderResource rule = proxy with { Kind = MihomoProviderKind.Rule };
        MihomoProviderResource upperCase = proxy with { Name = "Shared" };
        FakeProxyRuntimeController runtime = new() { ProviderResources = [proxy, rule, upperCase] };
        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), new FakeProxyCatalog(),
            new FakeProxyLatency(), runtime, new FakeProxiesLog(), new TestApplicationErrorSink(),
            new ModelDisplayMapper(static text => text));
        await viewModel.RefreshRuntimeAsync(CancellationToken.None);
        MihomoProviderResourceDisplay[] original = [.. viewModel.ProviderResources];
        runtime.ProviderResources = [upperCase, rule with { ItemCount = 8 }, proxy];

        await viewModel.RefreshRuntimeAsync(CancellationToken.None);

        Assert.Same(original[2], viewModel.ProviderResources[0]);
        Assert.Same(original[1], viewModel.ProviderResources[1]);
        Assert.Same(original[0], viewModel.ProviderResources[2]);
        Assert.Equal(8, original[1].Model.ItemCount);
        Assert.Equal(2, original[0].Model.ItemCount);
        runtime.ProviderResources = [rule, proxy with { Name = "new" }];
        await viewModel.RefreshRuntimeAsync(CancellationToken.None);
        Assert.Equal(2, viewModel.ProviderResources.Count);
        Assert.Same(original[1], viewModel.ProviderResources[0]);
        Assert.Equal("new", viewModel.ProviderResources[1].Model.Name);
        Assert.DoesNotContain(original[0], viewModel.ProviderResources);
        Assert.DoesNotContain(original[2], viewModel.ProviderResources);
    }

    /// <summary>Fake localization provider for proxies tests.</summary>
    private sealed class FakeProxiesLocalization : IProxiesLocalization
    {
        /// <summary>Gets a localized string for a key.</summary>
        /// <param name="key">Localization key. Must not be null.</param>
        /// <returns>Localized test string.</returns>
        public string GetString(string key)
        {
            return key switch
            {
                "Nav.ProxyNodes" => "Proxy nodes",
                "Page.ProxyNodes.Description" => "Description",
                "Command.Refresh" => "Refresh",
                "ProxyNodes.Command.RefreshRuntime" => "Refresh runtime",
                "Command.TestLatency" => "Test latency",
                "ProxyNodes.Section.StrategyGroups" => "Strategy groups",
                "ProxyNodes.Section.Resources" => "Resources",
                "ProxyNodes.Status.RuntimeNotRefreshed" => "Runtime not refreshed",
                "ProxyNodes.Status.RuntimeRefreshed" => "Runtime refreshed",
                "ProxyNodes.Status.SelectionApplied" => "Selection applied",
                "ProxyNodes.Status.ProviderUpdated" => "Provider updated",
                "ProxyNodes.Status.ProviderUpdatedEmpty" => "Provider updated with no entries",
                "ProxyNodes.Status.RuntimeUnavailable" => "Runtime unavailable",
                _ => key,
            };
        }
    }

    /// <summary>Fake proxy catalog for proxies tests.</summary>
    private sealed class FakeProxyCatalog : IProxyNodeCatalog
    {
        /// <summary>Gets or sets fake proxy nodes returned by the catalog.</summary>
        /// <value>Current fake node list.</value>
        public IReadOnlyList<ProxyNode> Nodes { get; set; } =
        [
            new("Direct", "DIRECT", new RegionMetadata("CN", "China", "cn"), 0),
        ];

        public int ReadCount { get; private set; }

        /// <summary>Gets fake proxy nodes.</summary>
        /// <returns>Configured proxy nodes.</returns>
        public IReadOnlyList<ProxyNode> GetNodes()
        {
            ReadCount++;
            return Nodes;
        }
    }

    /// <summary>Fake latency tester for proxies tests.</summary>
    private sealed class FakeProxyLatency : IProxyLatencyTester
    {
        /// <summary>Gets or sets fake tested nodes returned by latency testing.</summary>
        /// <value>Configured tested nodes; null returns the input nodes.</value>
        public IReadOnlyList<ProxyNode>? TestedNodes { get; set; }

        /// <summary>Gets or sets an exception to throw while testing.</summary>
        /// <value>Exception thrown when non-null.</value>
        public Exception? ExceptionToThrow { get; set; }

        /// <summary>Tests fake proxy nodes.</summary>
        /// <param name="nodes">Input nodes. Must not be null.</param>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured tested nodes or the input nodes.</returns>
        public Task<IReadOnlyList<ProxyNode>> TestNodesAsync(IReadOnlyList<ProxyNode> nodes, CancellationToken cancellationToken)
        {
            return ExceptionToThrow is null
                ? Task.FromResult(TestedNodes ?? nodes)
                : Task.FromException<IReadOnlyList<ProxyNode>>(ExceptionToThrow);
        }
    }

    /// <summary>Fake runtime controller for strategy groups and providers.</summary>
    private sealed class FakeProxyRuntimeController : IProxyRuntimeController
    {
        /// <summary>Gets fake strategy groups.</summary>
        /// <value>Configured fake strategy groups.</value>
        public IReadOnlyList<MihomoProxyGroup> ProxyGroups { get; } =
        [
            new("Proxy", "Selector", "Node A", ["Node A", "Node B", "DIRECT"]),
        ];

        /// <summary>Gets fake provider resources.</summary>
        /// <value>Configured fake provider resources.</value>
        public IReadOnlyList<MihomoProviderResource> ProviderResources { get; set; } =
        [
            new("sub", MihomoProviderKind.Proxy, "HTTP", string.Empty, 2, DateTimeOffset.UnixEpoch),
            new("reject", MihomoProviderKind.Rule, string.Empty, "domain", 123, DateTimeOffset.UnixEpoch),
        ];

        /// <summary>Gets last selected group and proxy.</summary>
        /// <value>Last selection tuple.</value>
        public (string GroupName, string ProxyName)? LastSelection { get; private set; }

        /// <summary>Gets last updated provider.</summary>
        /// <value>Last updated provider.</value>
        public MihomoProviderResource? LastUpdatedProvider { get; private set; }

        /// <summary>Gets refresh call count.</summary>
        /// <value>Refresh call count.</value>
        public int RefreshCount { get; private set; }

        public Exception? RefreshFailure { get; set; }

        /// <summary>Gets fake strategy groups.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured groups.</returns>
        public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.FromResult(ProxyGroups);
        }

        /// <summary>Gets fake provider resources.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured resources.</returns>
        public Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken)
        {
            return RefreshFailure is null ? Task.FromResult(ProviderResources)
                : Task.FromException<IReadOnlyList<MihomoProviderResource>>(RefreshFailure);
        }

        /// <summary>Captures one fake strategy group selection.</summary>
        /// <param name="groupName">Group name. Must not be null.</param>
        /// <param name="proxyName">Proxy name. Must not be null.</param>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Completed task.</returns>
        public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
        {
            LastSelection = (groupName, proxyName);
            return Task.CompletedTask;
        }

        /// <summary>Captures one fake provider update.</summary>
        /// <param name="provider">Provider to update.</param>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Completed task.</returns>
        public Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
        {
            LastUpdatedProvider = provider;
            return Task.CompletedTask;
        }
    }

    /// <summary>Fake log sink for proxies tests.</summary>
    private sealed class FakeProxiesLog : IProxiesLog
    {
        /// <summary>Gets captured log entries.</summary>
        /// <value>Mutable list of captured entries.</value>
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
