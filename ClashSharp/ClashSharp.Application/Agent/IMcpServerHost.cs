using ClashSharp.Agent;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Owns one loopback MCP listener and awaits its complete shutdown.</summary>
public interface IMcpServerHost : IAsyncDisposable
{
    /// <summary>Gets the independently observed endpoint, or null when no listener is active.</summary>
    Uri? BoundEndpoint { get; }

    /// <summary>Starts the requested enabled endpoint; an existing listener must be stopped before changing its port.</summary>
    /// <param name="configuration">Validated port and local user policy.</param>
    /// <param name="cancellationToken">Cancels startup before a successful listener is published.</param>
    Task StartAsync(McpServerConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>Stops admission to the endpoint and awaits its owned requests before returning.</summary>
    /// <param name="cancellationToken">Cancels waiting without discarding ownership of a still-running host.</param>
    Task StopAsync(CancellationToken cancellationToken);
}
