using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Active connection API contract used by <see cref="ConnectionsViewModel"/>.</summary>
internal interface IActiveConnectionClient
{
    Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken);

    Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken);

    Task CloseAllConnectionsAsync(CancellationToken cancellationToken);
}
