using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using ClashSharp.Model;
using ClashSharp.Tests.Unit.ViewModel;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Contract of the proxies page model: nodes, strategy groups, provider resources and the page status
/// follow the last observation through any mix of refreshes, selections, updates, failures, latency
/// tests, data-directory changes and language changes; surviving provider rows keep their identity.
/// </summary>
public sealed class ProxiesViewModelContractTests
{
    private enum Operation
    {
        RefreshNodes,
        RefreshNodesFailing,
        RuntimeActive,
        RuntimeInactive,
        RuntimeFailing,
        SelectValid,
        SelectFailing,
        UpdateProvider,
        UpdateProviderFailing,
        UpdateProviderVanishing,
        LatencyTest,
        LatencyTestFailing,
        ReloadForDataChange,
        ChangeLanguage,
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
    public async Task RandomUserSessions_KeepNodesGroupsProvidersAndStatusConsistent(int seed)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Random random = new(20261109 + seed);
        MutablePageLocalization localization = new() { Language = ViewModelContractSupport.AllLanguages[seed % ViewModelContractSupport.AllLanguages.Length] };
        FakeRuntime runtime = new();
        FakeCatalog catalog = new() { Nodes = RandomNodes(random, 0) };
        FakeLatency latency = new();
        TestApplicationErrorSink errors = new();
        ProxiesViewModel page = new(localization, catalog, latency, runtime, new NullLog(), errors, new ModelDisplayMapper(text => text));
        List<ProxyNode> expectedNodes = [];
        List<MihomoProxyGroup> expectedGroups = [];
        List<MihomoProviderResource> expectedProviders = [];
        Func<string> status = () => localization.GetString("ProxyNodes.Status.RuntimeNotRefreshed");
        int expectedCatalogErrors = 0;
        Operation[] operations = Enum.GetValues<Operation>();

        for (int step = 0; step < 120; step++)
        {
            Operation operation = operations[random.Next(operations.Length)];
            string label = $"seed {seed} step {step} {operation}";
            Dictionary<(MihomoProviderKind, string), MihomoProviderResourceDisplay> rowsBefore = page.ProviderResources
                .ToDictionary(row => (row.Model.Kind, row.Model.Name));
            switch (operation)
            {
                case Operation.RefreshNodes:
                    catalog.Nodes = RandomNodes(random, step);
                    await page.RefreshNodesCommand.ExecuteObservedAsync(null, CancellationToken.None);
                    expectedNodes = [.. catalog.Nodes];
                    break;
                case Operation.RefreshNodesFailing:
                    catalog.FailNext = true;
                    await page.RefreshNodesCommand.ExecuteObservedAsync(null, CancellationToken.None);
                    expectedCatalogErrors++;
                    break;
                case Operation.RuntimeActive:
                    runtime.Active = true;
                    runtime.Groups = RandomGroups(random, step);
                    runtime.Providers = RandomProviders(random, step, rowsBefore.Keys);
                    await page.RefreshRuntimeCommand.ExecuteObservedAsync(null, CancellationToken.None);
                    expectedGroups = [.. runtime.Groups];
                    expectedProviders = [.. runtime.Providers];
                    status = () => localization.GetString("ProxyNodes.Status.RuntimeRefreshed");
                    break;
                case Operation.RuntimeInactive:
                    runtime.Active = false;
                    await page.RefreshRuntimeCommand.ExecuteObservedAsync(null, CancellationToken.None);
                    expectedGroups = [];
                    expectedProviders = [];
                    status = () => localization.GetString("ProxyNodes.Status.RuntimeStopped");
                    break;
                case Operation.RuntimeFailing:
                    runtime.Active = true;
                    runtime.FailNext = new HttpRequestException("controller down");
                    await page.RefreshRuntimeCommand.ExecuteObservedAsync(null, CancellationToken.None);
                    expectedGroups = [];
                    expectedProviders = [];
                    status = () => localization.GetString("ProxyNodes.Status.RuntimeUnavailable");
                    break;
                case Operation.SelectValid:
                case Operation.SelectFailing:
                    {
                        if (page.ProxyGroups.Count == 0)
                        {
                            break;
                        }

                        MihomoProxyGroupDisplay group = page.ProxyGroups[random.Next(page.ProxyGroups.Count)];
                        string proxy = group.Candidates[random.Next(group.Candidates.Count)];
                        InvalidOperationException? failure = operation == Operation.SelectFailing ? new("selector rejected") : null;
                        runtime.FailNext = failure;
                        await page.SelectProxyCommand.ExecuteObservedAsync(new ProxyGroupSelectionRequest(group.Model, proxy), CancellationToken.None);
                        if (failure is null)
                        {
                            expectedGroups = [.. runtime.Groups];
                            expectedProviders = [.. runtime.Providers];
                            status = () => localization.GetString("ProxyNodes.Status.SelectionApplied");
                        }
                        else
                        {
                            // The failure arrives before any reload: the groups stay as they were.
                            string code = RuntimeFailureDiagnostics.ExtractCode(failure, RuntimeFailureDiagnostics.ControllerUnavailable);
                            status = () => RuntimeFailureDiagnostics.Format(
                                code,
                                localization.GetString,
                                localization.GetString("ProxyNodes.Status.RuntimeUnavailable"));
                        }

                        break;
                    }

                case Operation.UpdateProvider:
                case Operation.UpdateProviderFailing:
                case Operation.UpdateProviderVanishing:
                    {
                        if (page.ProviderResources.Count == 0)
                        {
                            break;
                        }

                        MihomoProviderResourceDisplay provider = page.ProviderResources[random.Next(page.ProviderResources.Count)];
                        runtime.OnUpdate = operation switch
                        {
                            Operation.UpdateProviderFailing => _ => throw new InvalidOperationException("update rejected"),
                            Operation.UpdateProviderVanishing => target => runtime.Providers = [.. runtime.Providers
                            .Where(candidate => candidate.Kind != target.Kind || candidate.Name != target.Name)],
                            _ => target => runtime.Providers = [.. runtime.Providers.Select(candidate =>
                            candidate.Kind == target.Kind && candidate.Name == target.Name
                                ? candidate with { ItemCount = random.Next(0, 3) }
                                : candidate)],
                        };
                        await page.UpdateProviderCommand.ExecuteObservedAsync(provider, CancellationToken.None);
                        runtime.OnUpdate = null;
                        if (operation == Operation.UpdateProviderFailing)
                        {
                            string code = RuntimeFailureDiagnostics.ExtractCode(
                                new InvalidOperationException("x"),
                                RuntimeFailureDiagnostics.ProviderUpdateFailed);
                            status = () => RuntimeFailureDiagnostics.Format(
                                code,
                                localization.GetString,
                                localization.GetString("ProxyNodes.Status.RuntimeUnavailable"));
                        }
                        else if (operation == Operation.UpdateProviderVanishing)
                        {
                            expectedGroups = [.. runtime.Groups];
                            expectedProviders = [.. runtime.Providers];
                            status = () => RuntimeFailureDiagnostics.Format(
                                RuntimeFailureDiagnostics.ProviderUpdateFailed,
                                localization.GetString,
                                localization.GetString("ProxyNodes.Status.RuntimeUnavailable"));
                        }
                        else
                        {
                            expectedGroups = [.. runtime.Groups];
                            expectedProviders = [.. runtime.Providers];
                            MihomoProviderResource updated = runtime.Providers.Single(candidate =>
                                candidate.Kind == provider.Model.Kind && candidate.Name == provider.Model.Name);
                            string key = updated.ItemCount == 0 ? "ProxyNodes.Status.ProviderUpdatedEmpty" : "ProxyNodes.Status.ProviderUpdated";
                            status = () => localization.GetString(key);
                        }

                        break;
                    }

                case Operation.LatencyTest:
                    latency.FailNext = false;
                    await page.TestLatencyCommand.ExecuteObservedAsync(null, CancellationToken.None);
                    expectedNodes = [.. latency.LastResult];
                    break;
                case Operation.LatencyTestFailing:
                    latency.FailNext = true;
                    await page.TestLatencyCommand.ExecuteObservedAsync(null, CancellationToken.None);
                    latency.FailNext = false;
                    break;
                case Operation.ReloadForDataChange:
                    catalog.Nodes = RandomNodes(random, step);
                    runtime.Active = random.Next(3) != 0;
                    runtime.Groups = RandomGroups(random, step);
                    runtime.Providers = RandomProviders(random, step, []);
                    await page.ReloadForDataChangeAsync(CancellationToken.None);
                    expectedNodes = [.. catalog.Nodes];
                    expectedGroups = runtime.Active ? [.. runtime.Groups] : [];
                    expectedProviders = runtime.Active ? [.. runtime.Providers] : [];
                    status = runtime.Active
                        ? () => localization.GetString("ProxyNodes.Status.RuntimeRefreshed")
                        : () => localization.GetString("ProxyNodes.Status.RuntimeStopped");
                    break;
                default:
                    localization.Language = ViewModelContractSupport.AllLanguages[random.Next(ViewModelContractSupport.AllLanguages.Length)];
                    page.RefreshLocalization();
                    break;
            }

            runtime.FailNext = null;
            AssertConsistent(page, localization, expectedNodes, expectedGroups, expectedProviders, status(), rowsBefore, operation, label);
        }

        Assert.Equal(expectedCatalogErrors, errors.Errors.Count);
        Assert.All(errors.Errors, error => Assert.Equal("proxies-refresh-nodes", error.OperationName));
    }

    [Fact]
    public async Task LatencyTest_ShowsRunningStatusWithoutLosingTheRuntimeObservation_AndAnnouncesBothTransitions()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        FakeRuntime runtime = new() { Active = true, Groups = RandomGroups(new Random(1), 1) };
        FakeLatency latency = new() { Gate = new TaskCompletionSource() };
        FakeCatalog catalog = new() { Nodes = RandomNodes(new Random(2), 1) };
        ProxiesViewModel page = new(localization, catalog, latency, runtime, new NullLog(), new TestApplicationErrorSink(), new ModelDisplayMapper(text => text));
        await page.LoadAsync(CancellationToken.None);
        string idleStatus = page.PageStatusText;
        Assert.Equal(page.RuntimeStatusText, idleStatus);
        List<string> announced = [];
        ((INotifyPropertyChanged)page).PropertyChanged += (_, args) => announced.Add(args.PropertyName ?? string.Empty);

        Task run = page.TestLatencyCommand.ExecuteObservedAsync(null, CancellationToken.None);

        Assert.Equal(localization.GetString("Master.LatencyDialog.Running"), page.PageStatusText);
        Assert.Equal(idleStatus, page.RuntimeStatusText);
        Assert.Contains(nameof(ProxiesViewModel.PageStatusText), announced);
        announced.Clear();
        latency.Gate.SetResult();
        await run;

        Assert.Equal(page.RuntimeStatusText, page.PageStatusText);
        Assert.Contains(nameof(ProxiesViewModel.PageStatusText), announced);
        Assert.True(page.TestLatencyCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelledLatencyTest_ReleasesTheCommandKeepsTheNodesAndReportsNothing()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        FakeLatency latency = new() { WaitForCancellation = true };
        TestApplicationErrorSink errors = new();
        FakeCatalog catalog = new() { Nodes = RandomNodes(new Random(3), 1) };
        ProxiesViewModel page = new(localization, catalog, latency, new FakeRuntime(), new NullLog(), errors, new ModelDisplayMapper(text => text));
        await page.RefreshNodesAsync(CancellationToken.None);
        string[] before = [.. page.ProxyNodes.Select(node => node.Model.Name)];
        using CancellationTokenSource cancellation = new();

        Task run = page.TestLatencyCommand.ExecuteObservedAsync(null, cancellation.Token);
        Assert.True(page.TestLatencyCommand.IsRunning);
        cancellation.Cancel();
        await run;

        Assert.False(page.TestLatencyCommand.IsRunning);
        Assert.True(page.TestLatencyCommand.CanExecute(null));
        Assert.Equal(before, page.ProxyNodes.Select(node => node.Model.Name).ToArray());
        Assert.Empty(errors.Errors);
        Assert.Equal(page.RuntimeStatusText, page.PageStatusText);
    }

    [Fact]
    public async Task ACancelledOrFailedLoad_NeverClearsWhatThePageAlreadyShows()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        FakeCatalog catalog = new() { Nodes = RandomNodes(new Random(4), 1) };
        FakeRuntime runtime = new() { Active = true, Groups = RandomGroups(new Random(5), 1), Providers = RandomProviders(new Random(6), 1, []) };
        TestApplicationErrorSink errors = new();
        ProxiesViewModel page = new(localization, catalog, new FakeLatency(), runtime, new NullLog(), errors, new ModelDisplayMapper(text => text));
        await page.LoadAsync(CancellationToken.None);
        string[] nodes = [.. page.ProxyNodes.Select(node => node.Model.Name)];
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await page.RefreshNodesAsync(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.RefreshRuntimeAsync(cancelled.Token));
        catalog.FailNext = true;
        await page.RefreshNodesAsync(CancellationToken.None);

        Assert.Equal(nodes, page.ProxyNodes.Select(node => node.Model.Name).ToArray());
        Assert.Single(errors.Errors);
        Assert.NotEmpty(page.ProxyGroups);
    }

    /// <summary>Gets one row per language.</summary>
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
            foreach (bool active in new[] { true, false })
            {
                (ProxiesViewModel switched, MutablePageLocalization switchedLocalization) = await BuildAsync(from, active);
                (ProxiesViewModel fresh, _) = await BuildAsync(to, active);

                ViewModelContractSupport.AssertSwitchEqualsFreshAndAnnouncesEveryChange(
                    $"{from}->{to} active:{active}",
                    switched,
                    fresh,
                    () =>
                    {
                        switchedLocalization.Language = to;
                        switched.RefreshLocalization();
                    });

                Assert.Equal(
                    fresh.ProxyNodes.Select(node => node.LatencyDisplay).ToArray(),
                    switched.ProxyNodes.Select(node => node.LatencyDisplay).ToArray());
                Assert.Equal(
                    fresh.ProxyGroups.Select(group => group.TypeDisplay).ToArray(),
                    switched.ProxyGroups.Select(group => group.TypeDisplay).ToArray());
                Assert.Equal(
                    fresh.ProviderResources.Select(provider => provider.UpdateActionText).ToArray(),
                    switched.ProviderResources.Select(provider => provider.UpdateActionText).ToArray());
            }
        }
    }

    [Fact]
    public async Task LargeCollections_AreMappedCompletelyAndProviderRowsSurviveAReorder()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MutablePageLocalization localization = new();
        List<ProxyNode> nodes = [.. Enumerable.Range(0, 5000).Select(index => new ProxyNode(
            "node-" + index, index % 9 == 0 ? "DIRECT" : "ss", new RegionMetadata("US", "United States", "us"),
            index % 4 == 0 ? null : index, "h" + index, 443, index % 3 != 0))];
        List<MihomoProviderResource> providers = [.. Enumerable.Range(0, 400).Select(index => new MihomoProviderResource(
            "p" + index, index % 2 == 0 ? MihomoProviderKind.Proxy : MihomoProviderKind.Rule, "HTTP", "domain", index, DateTimeOffset.UnixEpoch))];
        FakeRuntime runtime = new() { Active = true, Providers = providers, Groups = [new("g", "Selector", "a", ["a", "b"])] };
        ProxiesViewModel page = new(localization, new FakeCatalog { Nodes = nodes }, new FakeLatency(), runtime, new NullLog(), new TestApplicationErrorSink(), new ModelDisplayMapper(text => text));
        await page.LoadAsync(CancellationToken.None);
        Dictionary<(MihomoProviderKind, string), MihomoProviderResourceDisplay> rows = page.ProviderResources
            .ToDictionary(row => (row.Model.Kind, row.Model.Name));

        runtime.Providers = [.. providers.AsEnumerable().Reverse().Where(provider => provider.ItemCount % 5 != 0)];
        await page.RefreshRuntimeAsync(CancellationToken.None);

        Assert.Equal(5000, page.ProxyNodes.Count);
        Assert.Equal(runtime.Providers.Select(provider => (provider.Kind, provider.Name)).ToArray(),
            page.ProviderResources.Select(row => (row.Model.Kind, row.Model.Name)).ToArray());
        foreach (MihomoProviderResourceDisplay row in page.ProviderResources)
        {
            Assert.Same(rows[(row.Model.Kind, row.Model.Name)], row);
        }
    }

    private static void AssertConsistent(
        ProxiesViewModel page,
        MutablePageLocalization localization,
        List<ProxyNode> expectedNodes,
        List<MihomoProxyGroup> expectedGroups,
        List<MihomoProviderResource> expectedProviders,
        string expectedStatus,
        Dictionary<(MihomoProviderKind, string), MihomoProviderResourceDisplay> rowsBefore,
        Operation operation,
        string label)
    {
        foreach (AsyncRelayCommand command in new[]
                 {
                     page.RefreshNodesCommand, page.TestLatencyCommand, page.RefreshRuntimeCommand,
                     page.SelectProxyCommand, page.UpdateProviderCommand,
                 })
        {
            Assert.True(command.CanExecute(null), label);
            Assert.False(command.IsRunning, label);
        }

        Assert.Equal(expectedNodes.Select(node => node.Name).ToArray(), page.ProxyNodes.Select(node => node.Model.Name).ToArray());
        Assert.Equal(expectedNodes.Count > 0, page.HasProxyNodes);
        Assert.Equal(expectedNodes.Count == 0, page.HasNoProxyNodes);
        foreach (ProxyNodeDisplay row in page.ProxyNodes)
        {
            Assert.True(
                ProxyNodeDisplay.FormatLatency(row.Model, localization.GetString) == row.LatencyDisplay,
                $"{label}: stale latency text for {row.Model.Name}.");
        }

        Assert.Equal(
            expectedGroups.Select(group => (group.Name, group.CurrentSelection, string.Join("|", group.Candidates))).ToArray(),
            page.ProxyGroups.Select(group => (group.Model.Name, group.Model.CurrentSelection, string.Join("|", group.Model.Candidates))).ToArray());
        Assert.Equal(expectedGroups.Count > 0, page.HasProxyGroups);
        foreach (MihomoProxyGroupDisplay group in page.ProxyGroups)
        {
            string expectedType = group.Model.Type switch
            {
                "Selector" => localization.GetString("ProxyNodes.GroupType.Selector"),
                "URLTest" => localization.GetString("ProxyNodes.GroupType.UrlTest"),
                "Fallback" => localization.GetString("ProxyNodes.GroupType.Fallback"),
                _ => group.TypeDisplay,
            };
            Assert.True(expectedType == group.TypeDisplay, $"{label}: stale group type text.");
        }

        Assert.Equal(
            expectedProviders.Select(provider => (provider.Kind, provider.Name, provider.ItemCount)).ToArray(),
            page.ProviderResources.Select(row => (row.Model.Kind, row.Model.Name, row.Model.ItemCount)).ToArray());
        Assert.Equal(expectedProviders.Count > 0, page.HasProviderResources);
        foreach (MihomoProviderResourceDisplay row in page.ProviderResources)
        {
            Assert.True(
                $"{localization.GetString("Command.Update")} · {row.NameDisplay}" == row.UpdateActionText,
                $"{label}: stale provider action text.");
            // A provider that survives a refresh keeps its row object, so list position and focus survive.
            if (rowsBefore.TryGetValue((row.Model.Kind, row.Model.Name), out MihomoProviderResourceDisplay? previous))
            {
                Assert.True(
                    ReferenceEquals(previous, row),
                    $"{label}: provider row '{row.Model.Name}' was replaced instead of updated.");
            }
        }

        Assert.True(expectedStatus == page.RuntimeStatusText, $"{label}: status '{page.RuntimeStatusText}' != '{expectedStatus}'.");
        Assert.Equal(page.RuntimeStatusText, page.PageStatusText);
        Assert.Equal(localization.GetString("Nav.ProxyNodes"), page.PageTitleText);
        _ = operation;
    }

    private static async Task<(ProxiesViewModel Page, MutablePageLocalization Localization)> BuildAsync(
        AppLanguage language,
        bool active)
    {
        MutablePageLocalization localization = new() { Language = language };
        FakeRuntime runtime = new()
        {
            Active = active,
            Groups =
            [
                new("Auto", "URLTest", "a", ["a", "b"]),
                new("Pick", "Selector", "b", ["a", "b"]),
                new("Odd", "Custom Type", "a", ["a"]),
            ],
            Providers =
            [
                new("pp", MihomoProviderKind.Proxy, "HTTP", string.Empty, 3, DateTimeOffset.UnixEpoch),
                new("rr", MihomoProviderKind.Rule, "File", "domain", 0, null),
            ],
        };
        FakeCatalog catalog = new()
        {
            Nodes =
            [
                new("DIRECT", "DIRECT", new RegionMetadata("US", "United States", "us"), null),
                new("n1", "ss", new RegionMetadata("JP", "Japan", "jp"), 120, "h", 1, true),
                new("n2", "vmess", new RegionMetadata("JP", "Japan", "jp"), null, "h", 1, true),
                new("n3", "vmess", new RegionMetadata("JP", "Japan", "jp"), null, "", null, true),
                new("n4", "trojan", new RegionMetadata("JP", "Japan", "jp"), null),
            ],
        };
        ProxiesViewModel page = new(localization, catalog, new FakeLatency(), runtime, new NullLog(), new TestApplicationErrorSink(), new ModelDisplayMapper(text => text));
        await page.LoadAsync(CancellationToken.None);
        return (page, localization);
    }

    private static List<ProxyNode> RandomNodes(Random random, int salt)
    {
        string[] protocols = ["ss", "vmess", "trojan", "DIRECT", "hysteria2"];
        return [.. Enumerable.Range(0, random.Next(0, 30)).Select(index => new ProxyNode(
            $"n{salt}-{index}",
            protocols[random.Next(protocols.Length)],
            new RegionMetadata("US", "United States", "us"),
            random.Next(3) == 0 ? null : random.Next(1, 2000),
            random.Next(4) == 0 ? string.Empty : "host" + index,
            random.Next(4) == 0 ? null : 443,
            random.Next(2) == 0))];
    }

    private static List<MihomoProxyGroup> RandomGroups(Random random, int salt)
    {
        string[] types = ["Selector", "URLTest", "Fallback", "LoadBalance", "Relay", "Unknown", "Custom"];
        return [.. Enumerable.Range(0, random.Next(0, 6)).Select(index =>
        {
            string[] candidates = [.. Enumerable.Range(0, random.Next(1, 6)).Select(candidate => $"c{salt}-{index}-{candidate}")];
            return new MihomoProxyGroup($"g{salt}-{index}", types[random.Next(types.Length)], candidates[0], candidates);
        })];
    }

    private static List<MihomoProviderResource> RandomProviders(
        Random random,
        int salt,
        IEnumerable<(MihomoProviderKind Kind, string Name)> existing)
    {
        List<(MihomoProviderKind Kind, string Name)> keep = [.. existing.Where(_ => random.Next(2) == 0)];
        List<MihomoProviderResource> providers = [];
        foreach ((MihomoProviderKind kind, string name) in keep)
        {
            providers.Add(new MihomoProviderResource(name, kind, "HTTP", "domain", random.Next(0, 50), DateTimeOffset.UnixEpoch.AddSeconds(salt)));
        }

        for (int index = 0; index < random.Next(0, 4); index++)
        {
            MihomoProviderKind kind = random.Next(2) == 0 ? MihomoProviderKind.Proxy : MihomoProviderKind.Rule;
            string name = $"p{salt}-{index}";
            if (!providers.Any(provider => provider.Kind == kind && provider.Name == name))
            {
                providers.Add(new MihomoProviderResource(name, kind, "HTTP", "domain", random.Next(0, 50), null));
            }
        }

        return [.. providers.OrderBy(_ => random.Next())];
    }

    private sealed class NullLog : IProxiesLog
    {
        public void Append(string level, string category, string message, string? detail)
        {
        }
    }

    private sealed class FakeCatalog : IProxyNodeCatalog
    {
        internal List<ProxyNode> Nodes { get; set; } = [];

        internal bool FailNext { get; set; }

        public IReadOnlyList<ProxyNode> GetNodes()
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("catalog unreadable");
            }

            return [.. Nodes];
        }
    }

    private sealed class FakeLatency : IProxyLatencyTester
    {
        internal bool FailNext { get; set; }

        internal bool WaitForCancellation { get; set; }

        internal TaskCompletionSource? Gate { get; set; }

        internal IReadOnlyList<ProxyNode> LastResult { get; private set; } = [];

        public async Task<IReadOnlyList<ProxyNode>> TestNodesAsync(IReadOnlyList<ProxyNode> nodes, CancellationToken cancellationToken)
        {
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (FailNext)
            {
                throw new InvalidOperationException("latency probe unavailable");
            }

            LastResult = [.. nodes.Select((node, index) => node with
            {
                LatencyMilliseconds = index % 3 == 0 ? null : 10 + index,
                WasLatencyTested = true,
            })];
            return LastResult;
        }
    }

    private sealed class FakeRuntime : IProxyRuntimeController
    {
        internal bool Active { get; set; }

        internal List<MihomoProxyGroup> Groups { get; set; } = [];

        internal List<MihomoProviderResource> Providers { get; set; } = [];

        internal Exception? FailNext { get; set; }

        internal Action<MihomoProviderResource>? OnUpdate { get; set; }

        public Task<bool> IsRuntimeActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Active);

        public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
        {
            if (FailNext is not null)
            {
                Exception failure = FailNext;
                FailNext = null;
                return Task.FromException<IReadOnlyList<MihomoProxyGroup>>(failure);
            }

            return Task.FromResult<IReadOnlyList<MihomoProxyGroup>>([.. Groups]);
        }

        public Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MihomoProviderResource>>([.. Providers]);

        public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
        {
            if (FailNext is not null)
            {
                Exception failure = FailNext;
                FailNext = null;
                return Task.FromException(failure);
            }

            for (int index = 0; index < Groups.Count; index++)
            {
                if (Groups[index].Name == groupName)
                {
                    Groups[index] = Groups[index] with { CurrentSelection = proxyName };
                }
            }

            return Task.CompletedTask;
        }

        public Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
        {
            OnUpdate?.Invoke(provider);
            return Task.CompletedTask;
        }
    }
}
