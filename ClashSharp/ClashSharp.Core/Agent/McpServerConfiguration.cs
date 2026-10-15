namespace ClashSharp.Agent;

/// <summary>Contains user-owned endpoint configuration without authentication secrets.</summary>
public sealed record McpServerConfiguration
{
    /// <summary>Default loopback port for native agent connections.</summary>
    public const int DefaultPort = 17892;

    /// <summary>Creates a validated endpoint configuration.</summary>
    /// <param name="port">Non-privileged TCP port in the range 1024 through 65535.</param>
    /// <param name="access">Complete local user policy.</param>
    public McpServerConfiguration(int port, McpAccessPolicy access)
    {
        if (port is < 1024 or > 65535) { throw new ArgumentOutOfRangeException(nameof(port)); }
        Port = port;
        Access = access ?? throw new ArgumentNullException(nameof(access));
    }

    /// <summary>Gets the requested loopback TCP port.</summary>
    public int Port { get; }

    /// <summary>Gets the complete local user policy.</summary>
    public McpAccessPolicy Access { get; }

    /// <summary>Gets the local endpoint clients can use after the listener has started.</summary>
    public Uri Endpoint => new(FormattableString.Invariant($"http://127.0.0.1:{Port}/mcp"));
}
