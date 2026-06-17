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

    /// <summary>Log sink used for command outcomes.</summary>
    private readonly IProxiesLog _log;

    /// <summary>Backing field for <see cref="ProxyNodes"/>.</summary>
    private IReadOnlyList<ProxyNode> _proxyNodes = [];

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
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _latencyTester = latencyTester ?? throw new ArgumentNullException(nameof(latencyTester));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        RefreshNodesCommand = new RelayCommand(RefreshNodes);
        TestLatencyCommand = new AsyncRelayCommand(TestLatencyAsync);
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

    /// <summary>Gets the latency-test command label.</summary>
    /// <value>Localized command label; never null.</value>
    public string TestLatencyText => _localization.GetString("Command.TestLatency");

    /// <summary>Gets the visible proxy nodes.</summary>
    /// <value>Read-only node list; never null.</value>
    public IReadOnlyList<ProxyNode> ProxyNodes
    {
        get => _proxyNodes;
        private set => SetProperty(ref _proxyNodes, value);
    }

    /// <summary>Gets the command that refreshes nodes from the catalog.</summary>
    /// <value>Synchronous refresh command.</value>
    public RelayCommand RefreshNodesCommand { get; }

    /// <summary>Gets the command that tests latency for visible nodes.</summary>
    /// <value>Asynchronous latency command.</value>
    public AsyncRelayCommand TestLatencyCommand { get; }

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
            _log.Append("Info", "ProxyNodes", "Proxy node latency test completed.", $"{testedNodes.Count:N0} nodes tested.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            _log.Append("Warning", "ProxyNodes", "Proxy node latency test failed.", exception.Message);
        }
    }
}
