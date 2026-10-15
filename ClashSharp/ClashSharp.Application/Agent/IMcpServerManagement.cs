using ClashSharp.Agent;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Detached desired settings and independently observed local-agent runtime.</summary>
/// <param name="Desired">Durably requested configuration.</param>
/// <param name="Runtime">Actual listener and credential state without secret material.</param>
public sealed record McpSettingsSnapshot(McpServerConfiguration Desired, McpRuntimeSnapshot Runtime);

/// <summary>Provides explicit human management operations that are never exposed as MCP tools.</summary>
public interface IMcpServerManagement
{
    /// <summary>Reads the current generation's desired settings and actual runtime.</summary>
    Task<McpSettingsSnapshot> ReadAsync(CancellationToken cancellationToken);
    /// <summary>Applies a complete user-approved endpoint and permission configuration.</summary>
    Task<McpSettingsSnapshot> ApplyAsync(McpServerConfiguration configuration, CancellationToken cancellationToken);
    /// <summary>Copies a freshly verified client configuration containing a secret for an explicit user action.</summary>
    Task<string> GetClientConfigurationAsync(CancellationToken cancellationToken);
    /// <summary>Replaces the credential and durably disables the endpoint if replacement cannot be verified.</summary>
    Task<McpSettingsSnapshot> RotateCredentialAsync(CancellationToken cancellationToken);
}
