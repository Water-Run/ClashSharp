/*
 * Proxies ViewModel Tests
 * Verifies proxy node list and latency command behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/ProxiesViewModelTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the proxies view model.</summary>
public sealed class ProxiesViewModelTests
{
    /// <summary>Verifies construction loads localized command labels and initial nodes.</summary>
    [Fact]
    public void Constructor_LoadsLabelsAndNodes()
    {
        FakeProxyCatalog catalog = new();

        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), catalog, new FakeProxyLatency(), new FakeProxiesLog());

        Assert.Equal("Proxy nodes", viewModel.PageTitleText);
        Assert.Equal("Refresh", viewModel.RefreshNodesText);
        Assert.Equal("Test latency", viewModel.TestLatencyText);
        Assert.Equal("Strategy groups", viewModel.ProxyGroupsSectionTitleText);
        Assert.Equal("Resources", viewModel.ProviderResourcesSectionTitleText);
        Assert.Equal(catalog.Nodes, viewModel.ProxyNodes);
    }

    /// <summary>Verifies refresh replaces visible nodes from the catalog.</summary>
    [Fact]
    public void RefreshNodes_ReloadsCatalogNodes()
    {
        FakeProxyCatalog catalog = new();
        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), catalog, new FakeProxyLatency(), new FakeProxiesLog());
        IReadOnlyList<ProxyNode> updatedNodes =
        [
            new("Updated", "HTTPS", new RegionMetadata("US", "United States", "us"), null),
        ];
        catalog.Nodes = updatedNodes;

        viewModel.RefreshNodes();

        Assert.Equal(updatedNodes, viewModel.ProxyNodes);
    }

    /// <summary>Verifies latency testing updates visible nodes and logs success.</summary>
    [Fact]
    public async Task TestLatencyAsync_WhenSuccessful_UpdatesNodesAndLogs()
    {
        FakeProxyLatency latency = new();
        FakeProxiesLog log = new();
        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), new FakeProxyCatalog(), latency, log);
        IReadOnlyList<ProxyNode> testedNodes =
        [
            new("Direct", "DIRECT", new RegionMetadata("CN", "China", "cn"), 0),
        ];
        latency.TestedNodes = testedNodes;

        await viewModel.TestLatencyAsync(CancellationToken.None);

        Assert.Equal(testedNodes, viewModel.ProxyNodes);
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
        ProxiesViewModel viewModel = new(new FakeProxiesLocalization(), new FakeProxyCatalog(), latency, log);
        IReadOnlyList<ProxyNode> originalNodes = viewModel.ProxyNodes;

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
            new FakeProxiesLog());

        await viewModel.RefreshRuntimeAsync(CancellationToken.None);

        Assert.Equal(runtime.ProxyGroups, viewModel.ProxyGroups);
        Assert.Equal(runtime.ProviderResources, viewModel.ProviderResources);
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
            new FakeProxiesLog());
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
            new FakeProxiesLog());
        MihomoProviderResource provider = runtime.ProviderResources[0];

        await viewModel.UpdateProviderAsync(provider, CancellationToken.None);

        Assert.Equal(provider, runtime.LastUpdatedProvider);
        Assert.Equal(1, runtime.RefreshCount);
        Assert.Equal("Provider updated", viewModel.RuntimeStatusText);
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
                "Command.TestLatency" => "Test latency",
                "ProxyNodes.Section.StrategyGroups" => "Strategy groups",
                "ProxyNodes.Section.Resources" => "Resources",
                "ProxyNodes.Status.RuntimeNotRefreshed" => "Runtime not refreshed",
                "ProxyNodes.Status.RuntimeRefreshed" => "Runtime refreshed",
                "ProxyNodes.Status.SelectionApplied" => "Selection applied",
                "ProxyNodes.Status.ProviderUpdated" => "Provider updated",
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

        /// <summary>Gets fake proxy nodes.</summary>
        /// <returns>Configured proxy nodes.</returns>
        public IReadOnlyList<ProxyNode> GetNodes()
        {
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
        public IReadOnlyList<MihomoProviderResource> ProviderResources { get; } =
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
            return Task.FromResult(ProviderResources);
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
