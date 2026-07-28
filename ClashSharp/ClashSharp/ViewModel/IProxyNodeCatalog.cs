using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Proxy node catalog contract required by <see cref="ProxiesViewModel"/>.</summary>
internal interface IProxyNodeCatalog
{
    IReadOnlyList<ProxyNode> GetNodes();
}
