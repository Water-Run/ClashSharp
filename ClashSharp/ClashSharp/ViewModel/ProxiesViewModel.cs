using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

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

    /// <summary>Reports unexpected page-load failures.</summary>
    private readonly IApplicationErrorSink _errorSink;

    /// <summary>Maps runtime and catalog models to UI-only display rows.</summary>
    private readonly IModelDisplayMapper _displayMapper;

    /// <summary>Raw proxy node state retained for latency commands.</summary>
    private IReadOnlyList<ProxyNode> _proxyNodeModels = [];

    /// <summary>Backing field for <see cref="ProxyNodes"/>.</summary>
    private IReadOnlyList<ProxyNodeDisplay> _proxyNodes = [];

    /// <summary>Backing field for <see cref="ProxyGroups"/>.</summary>
    private IReadOnlyList<MihomoProxyGroupDisplay> _proxyGroups = [];

    /// <summary>Backing field for <see cref="ProviderResources"/>.</summary>
    private IReadOnlyList<MihomoProviderResourceDisplay> _providerResources = [];

    /// <summary>Backing field for <see cref="RuntimeStatusText"/>.</summary>
    private string _runtimeStatusText = string.Empty;

    /// <summary>Initializes a proxies view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="catalog">Proxy node catalog. Must not be null.</param>
    /// <param name="latencyTester">Latency tester. Must not be null.</param>
    /// <param name="log">Log sink. Must not be null.</param>
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <param name="displayMapper">UI display row mapper. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ProxiesViewModel(
        IProxiesLocalization localization,
        IProxyNodeCatalog catalog,
        IProxyLatencyTester latencyTester,
        IProxiesLog log,
        IApplicationErrorSink errorSink,
        IModelDisplayMapper displayMapper)
        : this(
            localization,
            catalog,
            latencyTester,
            EmptyProxyRuntimeController.Instance,
            log,
            errorSink,
            displayMapper)
    {
    }

    /// <summary>Initializes a proxies view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="catalog">Proxy node catalog. Must not be null.</param>
    /// <param name="latencyTester">Latency tester. Must not be null.</param>
    /// <param name="runtimeController">Runtime controller. Must not be null.</param>
    /// <param name="log">Log sink. Must not be null.</param>
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <param name="displayMapper">UI display row mapper. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ProxiesViewModel(
        IProxiesLocalization localization,
        IProxyNodeCatalog catalog,
        IProxyLatencyTester latencyTester,
        IProxyRuntimeController runtimeController,
        IProxiesLog log,
        IApplicationErrorSink errorSink,
        IModelDisplayMapper displayMapper)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _latencyTester = latencyTester ?? throw new ArgumentNullException(nameof(latencyTester));
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        _displayMapper = displayMapper ?? throw new ArgumentNullException(nameof(displayMapper));

        RefreshNodesCommand = new AsyncRelayCommand(
            RefreshNodesAsync,
            _errorSink,
            operationName: "proxies-refresh-nodes");
        TestLatencyCommand = new AsyncRelayCommand(
            TestLatencyAsync,
            _errorSink,
            operationName: "proxies-test-latency");
        RefreshRuntimeCommand = new AsyncRelayCommand(
            RefreshRuntimeAsync,
            _errorSink,
            operationName: "proxies-refresh-runtime");
        SelectProxyCommand = new AsyncRelayCommand(
            SelectProxyCommandAsync,
            _errorSink,
            operationName: "proxies-select");
        UpdateProviderCommand = new AsyncRelayCommand(
            UpdateProviderCommandAsync,
            _errorSink,
            operationName: "proxies-update-provider");
        RuntimeStatusText = _localization.GetString("ProxyNodes.Status.RuntimeNotRefreshed");
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
    public IReadOnlyList<ProxyNodeDisplay> ProxyNodes
    {
        get => _proxyNodes;
        private set => SetProperty(ref _proxyNodes, value);
    }

    /// <summary>Gets runtime strategy groups.</summary>
    /// <value>Runtime strategy groups; never null.</value>
    public IReadOnlyList<MihomoProxyGroupDisplay> ProxyGroups
    {
        get => _proxyGroups;
        private set => SetProperty(ref _proxyGroups, value);
    }

    /// <summary>Gets runtime provider resources.</summary>
    /// <value>Runtime provider resources; never null.</value>
    public IReadOnlyList<MihomoProviderResourceDisplay> ProviderResources
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
    /// <value>Asynchronous refresh command.</value>
    public AsyncRelayCommand RefreshNodesCommand { get; }

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

    /// <summary>Loads catalog and runtime state through the explicit page lifecycle.</summary>
    /// <param name="cancellationToken">Cancels this page-load attempt.</param>
    /// <returns>A task that completes after both snapshots have been applied.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await RefreshNodesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshRuntimeAsync(cancellationToken);
    }

    /// <summary>Refreshes visible proxy nodes without blocking the UI thread.</summary>
    /// <param name="cancellationToken">Cancels this catalog read.</param>
    /// <returns>A task that completes after the isolated snapshot is applied.</returns>
    public Task RefreshNodesAsync(CancellationToken cancellationToken)
    {
        return ViewModelLoadExecutor.ExecuteAsync(
            _catalog.GetNodes,
            ApplyProxyNodes,
            _errorSink,
            "proxies-refresh-nodes",
            cancellationToken);
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
            IReadOnlyList<ProxyNode> testedNodes = await _latencyTester.TestNodesAsync(
                _proxyNodeModels,
                cancellationToken);
            ApplyProxyNodes(testedNodes);
            _log.Append("Info", "ProxyNodes", string.Format(CultureInfo.CurrentCulture, _localization.GetString("Master.LatencyDialog.Completed.Format"), testedNodes.Count), null);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or InvalidOperationException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
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
            IReadOnlyList<MihomoProxyGroup> groups =
                await _runtimeController.GetProxyGroupsAsync(cancellationToken);
            IReadOnlyList<MihomoProviderResource> providers =
                await _runtimeController.GetProviderResourcesAsync(cancellationToken);
            ProxyGroups = MapProxyGroups(groups);
            ProviderResources = MapProviderResources(providers);
            RuntimeStatusText = _localization.GetString("ProxyNodes.Status.RuntimeRefreshed");
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or InvalidOperationException
                or System.Net.Http.HttpRequestException
                or System.Text.Json.JsonException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
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
        catch (Exception exception) when (
            exception is OperationCanceledException
                or InvalidOperationException
                or ArgumentException
                or System.Net.Http.HttpRequestException
                or System.Text.Json.JsonException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            string code = RuntimeFailureDiagnostics.ExtractCode(
                exception,
                RuntimeFailureDiagnostics.ControllerUnavailable);
            RuntimeStatusText = RuntimeFailureDiagnostics.Format(
                code,
                _localization.GetString,
                _localization.GetString("ProxyNodes.Status.RuntimeUnavailable"));
            _log.Append("Warning", "ProxyNodes", RuntimeStatusText, code);
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
        catch (Exception exception) when (
            exception is OperationCanceledException
                or InvalidOperationException
                or ArgumentException
                or System.Net.Http.HttpRequestException
                or System.Text.Json.JsonException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            string code = RuntimeFailureDiagnostics.ExtractCode(
                exception,
                RuntimeFailureDiagnostics.ProviderUpdateFailed);
            RuntimeStatusText = RuntimeFailureDiagnostics.Format(
                code,
                _localization.GetString,
                _localization.GetString("ProxyNodes.Status.RuntimeUnavailable"));
            _log.Append("Warning", "ProxyNodes", RuntimeStatusText, code);
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
        return parameter is MihomoProviderResourceDisplay provider
            ? UpdateProviderAsync(provider.Model, cancellationToken)
            : Task.CompletedTask;
    }

    private void ApplyProxyNodes(IReadOnlyList<ProxyNode> nodes)
    {
        _proxyNodeModels = nodes;
        List<ProxyNodeDisplay> rows = new(nodes.Count);
        foreach (ProxyNode node in nodes)
        {
            rows.Add(_displayMapper.Map(node));
        }

        ProxyNodes = rows;
    }

    private IReadOnlyList<MihomoProxyGroupDisplay> MapProxyGroups(
        IReadOnlyList<MihomoProxyGroup> groups)
    {
        List<MihomoProxyGroupDisplay> rows = new(groups.Count);
        foreach (MihomoProxyGroup group in groups)
        {
            rows.Add(_displayMapper.Map(group));
        }

        return rows;
    }

    private IReadOnlyList<MihomoProviderResourceDisplay> MapProviderResources(
        IReadOnlyList<MihomoProviderResource> providers)
    {
        List<MihomoProviderResourceDisplay> rows = new(providers.Count);
        foreach (MihomoProviderResource provider in providers)
        {
            rows.Add(_displayMapper.Map(provider));
        }

        return rows;
    }
}
