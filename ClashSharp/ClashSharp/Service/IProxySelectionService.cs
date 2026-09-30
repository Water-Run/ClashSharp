using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Service;

/// <summary>Provides complete user selection and already-admitted restoration operations.</summary>
internal interface IProxySelectionService : INetworkTakeoverProxySelections
{
    Task SelectAsync(string groupName, string proxyName, CancellationToken cancellationToken);
}
