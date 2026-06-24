/*
 * Proxies Adapters
 * Connects proxies view model contracts to application singleton services
 *
 * @author: WaterRun
 * @file: ViewModel/ProxiesAdapters.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Adapts <see cref="LocalizationService"/> to proxies localization.</summary>
/// <remarks>
/// Invariants: Wraps a non-null localization service for the adapter lifetime.
/// Thread safety: Matches the wrapped service and is intended for UI-thread use.
/// Side effects: Reads localized resources from the wrapped service.
/// </remarks>
internal sealed class ProxiesLocalizationAdapter : IProxiesLocalization
{
    /// <summary>Wrapped localization service.</summary>
    private readonly LocalizationService _localization;

    /// <summary>Initializes a proxies localization adapter.</summary>
    /// <param name="localization">Localization service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localization"/> is null.</exception>
    public ProxiesLocalizationAdapter(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Resolved localized string or fallback key text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public string GetString(string key)
    {
        return _localization.GetString(key);
    }
}

/// <summary>Adapts <see cref="ProxyNodeCatalogService"/> to proxy node catalog reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null catalog service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads active profile data through the wrapped service.
/// </remarks>
internal sealed class ProxyNodeCatalogAdapter : IProxyNodeCatalog
{
    /// <summary>Wrapped proxy node catalog service.</summary>
    private readonly ProxyNodeCatalogService _catalog;

    /// <summary>Initializes a proxy node catalog adapter.</summary>
    /// <param name="catalog">Proxy node catalog service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public ProxyNodeCatalogAdapter(ProxyNodeCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>Gets proxy node rows for display.</summary>
    /// <returns>Read-only proxy node list.</returns>
    public IReadOnlyList<ProxyNode> GetNodes()
    {
        return _catalog.GetNodes();
    }
}

/// <summary>Adapts <see cref="ProxyLatencyService"/> to proxy latency testing.</summary>
/// <remarks>
/// Invariants: Wraps a non-null latency service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: May open network sockets and write node health data through the wrapped service.
/// </remarks>
internal sealed class ProxyLatencyTesterAdapter : IProxyLatencyTester
{
    /// <summary>Wrapped proxy latency service.</summary>
    private readonly ProxyLatencyService _latency;

    /// <summary>Initializes a proxy latency tester adapter.</summary>
    /// <param name="latency">Proxy latency service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="latency"/> is null.</exception>
    public ProxyLatencyTesterAdapter(ProxyLatencyService latency)
    {
        _latency = latency ?? throw new ArgumentNullException(nameof(latency));
    }

    /// <summary>Tests latency for the supplied nodes.</summary>
    /// <param name="nodes">Nodes to test. Must not be null.</param>
    /// <param name="cancellationToken">Cancels remaining tests when requested.</param>
    /// <returns>Node rows with updated latency values.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the wrapped service.
    /// Completion semantics: Does not mutate proxy configuration.
    /// </remarks>
    public Task<IReadOnlyList<ProxyNode>> TestNodesAsync(IReadOnlyList<ProxyNode> nodes, CancellationToken cancellationToken)
    {
        return _latency.TestNodesAsync(nodes, cancellationToken);
    }
}

/// <summary>Adapts <see cref="MihomoControllerClient"/> to runtime proxy controls.</summary>
internal sealed class ProxyRuntimeControllerAdapter : IProxyRuntimeController
{
    /// <summary>Wrapped controller client.</summary>
    private readonly MihomoControllerClient _controller;

    /// <summary>Initializes a runtime controller adapter.</summary>
    /// <param name="controller">Controller client. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="controller"/> is null.</exception>
    public ProxyRuntimeControllerAdapter(MihomoControllerClient controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    /// <summary>Gets runtime strategy groups from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>Runtime strategy groups.</returns>
    public Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
    {
        return _controller.GetProxyGroupsAsync(cancellationToken);
    }

    /// <summary>Gets runtime provider resources from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>Provider resources.</returns>
    public Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken)
    {
        return _controller.GetProviderResourcesAsync(cancellationToken);
    }

    /// <summary>Selects one proxy inside a runtime strategy group.</summary>
    /// <param name="groupName">Strategy group name. Must not be null.</param>
    /// <param name="proxyName">Proxy name. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>A task that completes after mihomo applies the selection.</returns>
    public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        return _controller.SelectProxyAsync(groupName, proxyName, cancellationToken);
    }

    /// <summary>Updates one runtime provider resource.</summary>
    /// <param name="provider">Provider resource to update.</param>
    /// <param name="cancellationToken">Cancels the local API request.</param>
    /// <returns>A task that completes after mihomo updates the provider.</returns>
    public Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
    {
        return _controller.UpdateProviderAsync(provider, cancellationToken);
    }
}

/// <summary>Adapts <see cref="LogStorageService"/> to proxies logging.</summary>
/// <remarks>
/// Invariants: Wraps a non-null log service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Writes log entries to persistent storage.
/// </remarks>
internal sealed class ProxiesLogAdapter : IProxiesLog
{
    /// <summary>Wrapped log service.</summary>
    private readonly LogStorageService _log;

    /// <summary>Initializes a proxies log adapter.</summary>
    /// <param name="log">Log service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.</exception>
    public ProxiesLogAdapter(LogStorageService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Appends one log entry.</summary>
    /// <param name="level">Log level. Must not be null.</param>
    /// <param name="category">Log category. Must not be null.</param>
    /// <param name="message">Log summary. Must not be null.</param>
    /// <param name="detail">Optional detail text; null when no detail exists.</param>
    public void Append(string level, string category, string message, string? detail)
    {
        _log.AppendLog(level, category, message, detail);
    }
}
