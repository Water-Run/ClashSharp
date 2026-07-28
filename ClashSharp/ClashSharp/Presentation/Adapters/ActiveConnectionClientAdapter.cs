using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="MihomoConnectionService"/> to active connection reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null connection service.
/// Thread safety: Matches the wrapped service.
/// Side effects: Calls the local mihomo external controller.
/// </remarks>
internal sealed class ActiveConnectionClientAdapter : IActiveConnectionClient
{
    /// <summary>Wrapped connection service.</summary>
    private readonly MihomoConnectionService _connections;

    /// <summary>Initializes an active connection client adapter.</summary>
    /// <param name="connections">Connection service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connections"/> is null.</exception>
    public ActiveConnectionClientAdapter(MihomoConnectionService connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>Gets active connection rows.</summary>
    /// <param name="cancellationToken">Cancels the local API request when requested.</param>
    /// <returns>Active connection rows.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the wrapped service.
    /// Completion semantics: Does not persist returned rows.
    /// </remarks>
    public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        return _connections.GetActiveConnectionsAsync(cancellationToken);
    }

    /// <summary>Closes one active connection.</summary>
    /// <param name="connectionId">Connection id. Must not be null or empty.</param>
    /// <param name="cancellationToken">Cancels the local API request when requested.</param>
    /// <returns>A task that completes after the connection is closed.</returns>
    public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        return _connections.CloseConnectionAsync(connectionId, cancellationToken);
    }

    /// <summary>Closes all active connections.</summary>
    /// <param name="cancellationToken">Cancels the local API request when requested.</param>
    /// <returns>A task that completes after mihomo closes all connections.</returns>
    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
    {
        return _connections.CloseAllConnectionsAsync(cancellationToken);
    }
}
