using System.Diagnostics.CodeAnalysis;
using ClashSharp.Agent;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Owns current authentication and complete admitted product operations behind the MCP transport.</summary>
public interface IMcpAgentBackend
{
    /// <summary>Captures the effective policy without I/O; tool discovery alone never authorizes an operation.</summary>
    McpAccessPolicy CapturePolicy();

    /// <summary>Verifies the current bearer secret without exposing it in the returned identity.</summary>
    /// <param name="secret">Untrusted bearer secret from the transport.</param>
    /// <param name="identity">Current verified credential identity, or null on failure.</param>
    /// <returns>Whether the current credential matches.</returns>
    bool TryAuthenticate(string secret, [NotNullWhen(true)] out McpClientIdentity? identity);

    /// <summary>Checks whether a previously authenticated identity still belongs to the active credential.</summary>
    /// <param name="identity">Identity created by transport authentication.</param>
    /// <returns>Whether the credential remains current.</returns>
    bool IsCurrentIdentity(McpClientIdentity identity);

    /// <summary>Executes one complete request under mutation admission and a retained data-generation pin.</summary>
    /// <remarks>Ordinary operations use ordinary admission; settings that drain producers acquire exclusive admission before any command gate.
    /// Implementations revalidate identity, all required scopes and every argument after admission, before side effects.</remarks>
    /// <param name="identity">Current authenticated identity, rechecked inside admission.</param>
    /// <param name="request">Fixed operation and untrusted detached arguments.</param>
    /// <param name="cancellationToken">Cancels waiting and uncommitted work; committed outcomes remain observable.</param>
    /// <returns>A bounded authorized response or a stable value-free failure.</returns>
    Task<McpToolResponse> ExecuteAsync(McpClientIdentity identity, McpToolRequest request, CancellationToken cancellationToken);
}
