/*
 * Mihomo Connection Service
 * Reads active connection rows from the mihomo external controller
 *
 * @author: WaterRun
 * @file: Service/MihomoConnectionService.cs
 * @date: 2026-06-15
 */

using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Reads active connection rows from the mihomo external controller.</summary>
/// <remarks>
/// Invariants: The service targets the local external-controller configured by <see cref="CoreConfigurationService"/>.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: Performs local HTTP requests against mihomo.
/// </remarks>
public sealed class MihomoConnectionService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="MihomoConnectionService"/> instance.</value>
    public static MihomoConnectionService Instance { get; } = new();

    /// <summary>Controller client used for mihomo requests.</summary>
    private readonly MihomoControllerClient _controllerClient;

    /// <summary>Initializes the connection service.</summary>
    private MihomoConnectionService()
        : this(MihomoControllerClient.Instance)
    {
    }

    /// <summary>Initializes a connection service with an explicit controller client.</summary>
    /// <param name="controllerClient">Controller client. Must not be null.</param>
    private MihomoConnectionService(MihomoControllerClient controllerClient)
    {
        _controllerClient = controllerClient;
    }

    /// <summary>Reads active connections from the local mihomo external controller.</summary>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>Active connection rows; empty when mihomo reports no active connections.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    /// <exception cref="JsonException">The mihomo API returns invalid JSON.</exception>
    public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        return await _controllerClient.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes one active connection through mihomo.</summary>
    /// <param name="connectionId">Connection id. Must not be null or empty.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>A task that completes after mihomo acknowledges the request.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is empty.</exception>
    public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        return _controllerClient.CloseConnectionAsync(connectionId, cancellationToken);
    }

    /// <summary>Closes all active connections through mihomo.</summary>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>A task that completes after mihomo acknowledges the request.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
    {
        return _controllerClient.CloseAllConnectionsAsync(cancellationToken);
    }
}
