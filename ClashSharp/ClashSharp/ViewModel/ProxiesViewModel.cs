/*
 * Proxies ViewModel
 * Owns bindable proxy node list and proxy commands
 *
 * @author: WaterRun
 * @file: ViewModel/ProxiesViewModel.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Localization contract required by <see cref="ProxiesViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations return a non-null string for every requested key.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: None required by the contract.
/// </remarks>
internal interface IProxiesLocalization
{
    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Resolved localized string, or a fallback string when the key is unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    string GetString(string key);
}

/// <summary>Proxy node catalog contract required by <see cref="ProxiesViewModel"/>.</summary>
/// <remarks>
/// Invariants: Returned lists and contained node strings are non-null.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May read active profile configuration.
/// </remarks>
internal interface IProxyNodeCatalog
{
    /// <summary>Gets proxy node rows for display.</summary>
    /// <returns>Read-only proxy node list.</returns>
    IReadOnlyList<ProxyNode> GetNodes();
}

/// <summary>Proxy latency contract required by <see cref="ProxiesViewModel"/>.</summary>
/// <remarks>
/// Invariants: Returned nodes correspond to the input nodes with latency values updated where available.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May open network sockets and record health statistics.
/// </remarks>
internal interface IProxyLatencyTester
{
    /// <summary>Tests latency for the supplied nodes.</summary>
    /// <param name="nodes">Nodes to test. Must not be null.</param>
    /// <param name="cancellationToken">Cancels remaining tests when requested.</param>
    /// <returns>Node rows with updated latency values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Latency testing cannot run.</exception>
    /// <remarks>
    /// Cancellation semantics: Cancellation stops remaining tests.
    /// Completion semantics: Does not mutate proxy configuration.
    /// </remarks>
    Task<IReadOnlyList<ProxyNode>> TestNodesAsync(IReadOnlyList<ProxyNode> nodes, CancellationToken cancellationToken);
}

/// <summary>Runtime proxy controller contract required by <see cref="ProxiesViewModel"/>.</summary>
internal interface IProxyRuntimeController
{
    /// <summary>Gets runtime strategy groups from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>Runtime strategy groups.</returns>
    Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken);

    /// <summary>Gets runtime provider resources from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>Provider resources.</returns>
    Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken);

    /// <summary>Selects one proxy inside a runtime strategy group.</summary>
    /// <param name="groupName">Strategy group name. Must not be null.</param>
    /// <param name="proxyName">Proxy name. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>A task that completes after mihomo applies the selection.</returns>
    Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken);

    /// <summary>Updates one runtime provider resource.</summary>
    /// <param name="provider">Provider resource to update.</param>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>A task that completes after mihomo updates the provider.</returns>
    Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken);
}

/// <summary>Fallback runtime controller used by legacy constructors.</summary>
internal sealed class EmptyProxyRuntimeController : IProxyRuntimeController
{
    public static EmptyProxyRuntimeController Instance { get; } = new();

    private EmptyProxyRuntimeController()
    {
    }

    public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<MihomoProxyGroup>>([]);
    }

    public Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<MihomoProviderResource>>([]);
    }

    public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>Logging contract required by <see cref="ProxiesViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations persist or discard each complete log entry atomically.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May write to persistent log storage.
/// </remarks>
internal interface IProxiesLog
{
    /// <summary>Appends one log entry.</summary>
    /// <param name="level">Log level. Must not be null.</param>
    /// <param name="category">Log category. Must not be null.</param>
    /// <param name="message">Log summary. Must not be null.</param>
    /// <param name="detail">Optional detail text; null when no detail exists.</param>
    /// <exception cref="ArgumentNullException"><paramref name="level"/>, <paramref name="category"/>, or <paramref name="message"/> is null.</exception>
    void Append(string level, string category, string message, string? detail);
}

/// <summary>Bindable view model for proxy node management.</summary>
/// <remarks>
/// Invariants: <see cref="ProxyNodes"/> is never null after construction.
/// Thread safety: Not thread-safe; intended for UI-thread binding and command execution.
/// Side effects: Commands call injected services that may read profiles, open network sockets, and write logs.
/// </remarks>
internal sealed class ProxiesViewModel : ObservableObject
{
    /// <summary>Localization provider used by visible text.</summary>
    private readonly IProxiesLocalization _localization;

    /// <summary>Proxy node catalog used for refreshes.</summary>
    private readonly IProxyNodeCatalog _catalog;

    /// <summary>Latency tester used by the test-latency command.</summary>
    private readonly IProxyLatencyTester _latencyTester;

    /// <summary>Runtime controller used for strategy groups and provider resources.</summary>
    private readonly IProxyRuntimeController _runtimeController;

    /// <summary>Log sink used for command outcomes.</summary>
    private readonly IProxiesLog _log;

    /// <summary>Backing field for <see cref="ProxyNodes"/>.</summary>
    private IReadOnlyList<ProxyNode> _proxyNodes = [];

    /// <summary>Backing field for <see cref="ProxyGroups"/>.</summary>
    private IReadOnlyList<MihomoProxyGroup> _proxyGroups = [];

    /// <summary>Backing field for <see cref="ProviderResources"/>.</summary>
    private IReadOnlyList<MihomoProviderResource> _providerResources = [];

    /// <summary>Backing field for <see cref="RuntimeStatusText"/>.</summary>
    private string _runtimeStatusText = string.Empty;

    /// <summary>Initializes a proxies view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="catalog">Proxy node catalog. Must not be null.</param>
    /// <param name="latencyTester">Latency tester. Must not be null.</param>
    /// <param name="log">Log sink. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ProxiesViewModel(
        IProxiesLocalization localization,
        IProxyNodeCatalog catalog,
        IProxyLatencyTester latencyTester,
        IProxiesLog log)
        : this(localization, catalog, latencyTester, EmptyProxyRuntimeController.Instance, log)
    {
    }

    /// <summary>Initializes a proxies view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="catalog">Proxy node catalog. Must not be null.</param>
    /// <param name="latencyTester">Latency tester. Must not be null.</param>
    /// <param name="runtimeController">Runtime controller. Must not be null.</param>
    /// <param name="log">Log sink. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ProxiesViewModel(
        IProxiesLocalization localization,
        IProxyNodeCatalog catalog,
        IProxyLatencyTester latencyTester,
        IProxyRuntimeController runtimeController,
        IProxiesLog log)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _latencyTester = latencyTester ?? throw new ArgumentNullException(nameof(latencyTester));
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        RefreshNodesCommand = new RelayCommand(RefreshNodes);
        TestLatencyCommand = new AsyncRelayCommand(TestLatencyAsync);
        RefreshRuntimeCommand = new AsyncRelayCommand(RefreshRuntimeAsync);
        SelectProxyCommand = new AsyncRelayCommand(SelectProxyCommandAsync);
        UpdateProviderCommand = new AsyncRelayCommand(UpdateProviderCommandAsync);
        RuntimeStatusText = _localization.GetString("ProxyNodes.Status.RuntimeNotRefreshed");
        RefreshNodes();
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title; never null.</value>
    public string PageTitleText => _localization.GetString("Nav.ProxyNodes");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description; never null.</value>
    public string DescriptionText => _localization.GetString("Page.ProxyNodes.Description");

    /// <summary>Gets the refresh command label.</summary>
    /// <value>Localized command label; never null.</value>
    public string RefreshNodesText => _localization.GetString("Command.Refresh");

    /// <summary>Gets the runtime refresh command label.</summary>
    /// <value>Localized command label; never null.</value>
    public string RefreshRuntimeText => _localization.GetString("ProxyNodes.Command.RefreshRuntime");

    /// <summary>Gets the latency-test command label.</summary>
    /// <value>Localized command label; never null.</value>
    public string TestLatencyText => _localization.GetString("Command.TestLatency");

    /// <summary>Gets the runtime strategy groups section title.</summary>
    /// <value>Localized section title.</value>
    public string ProxyGroupsSectionTitleText => _localization.GetString("ProxyNodes.Section.StrategyGroups");

    /// <summary>Gets the provider resources section title.</summary>
    /// <value>Localized section title.</value>
    public string ProviderResourcesSectionTitleText => _localization.GetString("ProxyNodes.Section.Resources");

    /// <summary>Gets the visible proxy nodes.</summary>
    /// <value>Read-only node list; never null.</value>
    public IReadOnlyList<ProxyNode> ProxyNodes
    {
        get => _proxyNodes;
        private set => SetProperty(ref _proxyNodes, value);
    }

    /// <summary>Gets runtime strategy groups.</summary>
    /// <value>Runtime strategy groups; never null.</value>
    public IReadOnlyList<MihomoProxyGroup> ProxyGroups
    {
        get => _proxyGroups;
        private set => SetProperty(ref _proxyGroups, value);
    }

    /// <summary>Gets runtime provider resources.</summary>
    /// <value>Runtime provider resources; never null.</value>
    public IReadOnlyList<MihomoProviderResource> ProviderResources
    {
        get => _providerResources;
        private set => SetProperty(ref _providerResources, value);
    }

    /// <summary>Gets runtime operation status text.</summary>
    /// <value>Status text; never null.</value>
    public string RuntimeStatusText
    {
        get => _runtimeStatusText;
        private set => SetProperty(ref _runtimeStatusText, value);
    }

    /// <summary>Gets the command that refreshes nodes from the catalog.</summary>
    /// <value>Synchronous refresh command.</value>
    public RelayCommand RefreshNodesCommand { get; }

    /// <summary>Gets the command that tests latency for visible nodes.</summary>
    /// <value>Asynchronous latency command.</value>
    public AsyncRelayCommand TestLatencyCommand { get; }

    /// <summary>Gets the command that refreshes runtime strategy groups and providers.</summary>
    /// <value>Asynchronous runtime refresh command.</value>
    public AsyncRelayCommand RefreshRuntimeCommand { get; }

    /// <summary>Gets the command that selects a proxy for a runtime strategy group.</summary>
    /// <value>Asynchronous runtime selection command.</value>
    public AsyncRelayCommand SelectProxyCommand { get; }

    /// <summary>Gets the command that updates a provider resource.</summary>
    /// <value>Asynchronous provider update command.</value>
    public AsyncRelayCommand UpdateProviderCommand { get; }

    /// <summary>Refreshes visible proxy nodes from the catalog.</summary>
    public void RefreshNodes()
    {
        ProxyNodes = _catalog.GetNodes();
    }

    /// <summary>Tests latency for visible proxy nodes and updates the list.</summary>
    /// <param name="cancellationToken">Cancels remaining latency tests when requested.</param>
    /// <returns>A task that completes after latency testing and logging finish.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the latency tester.
    /// Thread / reentrancy: UI callers should use <see cref="TestLatencyCommand"/> to prevent reentrancy.
    /// </remarks>
    public async Task TestLatencyAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ProxyNode> testedNodes = await _latencyTester.TestNodesAsync(ProxyNodes, cancellationToken);
            ProxyNodes = testedNodes;
            _log.Append("Info", "ProxyNodes", string.Format(_localization.GetString("Master.LatencyDialog.Completed.Format"), testedNodes.Count), null);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            _log.Append("Warning", "ProxyNodes", _localization.GetString("Master.LatencyDialog.Failed"), exception.Message);
        }
    }

    /// <summary>Refreshes runtime strategy groups and provider resources from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>A task that completes after the runtime state is loaded.</returns>
    public async Task RefreshRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProxyGroups = await _runtimeController.GetProxyGroupsAsync(cancellationToken);
            ProviderResources = await _runtimeController.GetProviderResourcesAsync(cancellationToken);
            RuntimeStatusText = _localization.GetString("ProxyNodes.Status.RuntimeRefreshed");
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        {
            ProxyGroups = [];
            ProviderResources = [];
            RuntimeStatusText = _localization.GetString("ProxyNodes.Status.RuntimeUnavailable");
            _log.Append("Warning", "ProxyNodes", RuntimeStatusText, exception.Message);
        }
    }

    /// <summary>Selects a proxy for a runtime strategy group and refreshes runtime state.</summary>
    /// <param name="group">Strategy group.</param>
    /// <param name="proxyName">Selected proxy name. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>A task that completes after selection and refresh finish.</returns>
    public async Task SelectProxyAsync(MihomoProxyGroup group, string proxyName, CancellationToken cancellationToken)
    {
        try
        {
            await _runtimeController.SelectProxyAsync(group.Name, proxyName, cancellationToken);
            await RefreshRuntimeAsync(cancellationToken);
            RuntimeStatusText = _localization.GetString("ProxyNodes.Status.SelectionApplied");
            _log.Append("Info", "ProxyNodes", RuntimeStatusText, $"{group.Name} -> {proxyName}");
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ArgumentException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        {
            RuntimeStatusText = _localization.GetString("ProxyNodes.Status.RuntimeUnavailable");
            _log.Append("Warning", "ProxyNodes", RuntimeStatusText, exception.Message);
        }
    }

    /// <summary>Updates one provider and refreshes runtime resources.</summary>
    /// <param name="provider">Provider resource to update.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>A task that completes after update and refresh finish.</returns>
    public async Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
    {
        try
        {
            await _runtimeController.UpdateProviderAsync(provider, cancellationToken);
            await RefreshRuntimeAsync(cancellationToken);
            RuntimeStatusText = _localization.GetString("ProxyNodes.Status.ProviderUpdated");
            _log.Append("Info", "ProxyNodes", RuntimeStatusText, provider.Name);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ArgumentException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        {
            RuntimeStatusText = _localization.GetString("ProxyNodes.Status.RuntimeUnavailable");
            _log.Append("Warning", "ProxyNodes", RuntimeStatusText, exception.Message);
        }
    }

    /// <summary>Selects a proxy from a command parameter tuple.</summary>
    private Task SelectProxyCommandAsync(object? parameter, CancellationToken cancellationToken)
    {
        return parameter is ProxyGroupSelectionRequest request
            ? SelectProxyAsync(request.Group, request.ProxyName, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Updates one provider from a command parameter.</summary>
    private Task UpdateProviderCommandAsync(object? parameter, CancellationToken cancellationToken)
    {
        return parameter is MihomoProviderResource provider
            ? UpdateProviderAsync(provider, cancellationToken)
            : Task.CompletedTask;
    }
}

/// <summary>Command payload for runtime proxy group selection.</summary>
/// <param name="Group">Runtime proxy group.</param>
/// <param name="ProxyName">Selected proxy name; never null.</param>
internal readonly record struct ProxyGroupSelectionRequest(MihomoProxyGroup Group, string ProxyName);
