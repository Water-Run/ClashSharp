using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Proxy latency contract required by <see cref="ProxiesViewModel"/>.</summary>
internal interface IProxyLatencyTester
{
    Task<IReadOnlyList<ProxyNode>> TestNodesAsync(
        IReadOnlyList<ProxyNode> nodes,
        CancellationToken cancellationToken);
}
