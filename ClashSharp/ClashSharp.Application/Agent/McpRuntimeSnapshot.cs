using ClashSharp.Agent;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Contains effective configuration and independently observed listener state without secrets.</summary>
/// <param name="Configuration">Last completely applied endpoint configuration.</param>
/// <param name="BoundEndpoint">Observed accepting endpoint, or null when unavailable.</param>
/// <param name="HasCredential">Whether a persisted credential was independently verified.</param>
/// <param name="IsSuspended">Whether the owning lifecycle has paused the endpoint.</param>
/// <param name="ErrorCode">Stable value-free failure code, or null.</param>
public sealed record McpRuntimeSnapshot(McpServerConfiguration Configuration, Uri? BoundEndpoint,
    bool HasCredential, bool IsSuspended, string? ErrorCode);
