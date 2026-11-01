using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="ProxyLatencyService"/> to proxy latency testing.</summary>
/// <remarks>
/// Invariants: Wraps a non-null latency service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: May open network sockets and write node health data through the wrapped service.
/// </remarks>
internal sealed class ProxyLatencyTesterAdapter : IProxyLatencyTester
{
    private readonly OperationNotificationRunner _notifications;

    /// <summary>Wrapped proxy latency service.</summary>
    private readonly ProxyLatencyService _latency;

    /// <summary>Initializes a proxy latency tester adapter.</summary>
    /// <param name="latency">Proxy latency service. Must not be null.</param>
    /// <param name="notifications">User-policy operation notifications.</param>
    /// <exception cref="ArgumentNullException"><paramref name="latency"/> is null.</exception>
    public ProxyLatencyTesterAdapter(ProxyLatencyService latency, IUserOperationNotifications? notifications = null)
    {
        _latency = latency ?? throw new ArgumentNullException(nameof(latency));
        _notifications = new(notifications);
    }

    /// <summary>Tests latency for the supplied nodes.</summary>
    /// <param name="nodes">Nodes to test. Must not be null.</param>
    /// <param name="cancellationToken">Cancels remaining tests when requested.</param>
    /// <returns>Node rows with updated latency values.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the wrapped service.
    /// Completion semantics: Does not mutate proxy configuration.
    /// </remarks>
    public Task<IReadOnlyList<ProxyNode>> TestNodesAsync(
        IReadOnlyList<ProxyNode> nodes,
        CancellationToken cancellationToken)
    {
        return _notifications.ExecuteAsync(() => _latency.TestNodesAsync(nodes, cancellationToken),
            "Nav.ProxyNodes", "Notification.Latency.Completed", "Notification.Latency.Incomplete", cancellationToken,
            tested => tested.Count == 0 ? null : tested.All(static node => !node.WasLatencyTested || node.LatencyMilliseconds.HasValue));
    }
}
