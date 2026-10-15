namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Owns one generation's protected MCP credential, excluded from preference exports.</summary>
public interface IMcpCredentialStore : IAsyncDisposable
{
    /// <summary>Reads the protected secret; malformed or unreadable storage must fail rather than appear absent.</summary>
    /// <param name="cancellationToken">Cancels uncommitted storage work.</param>
    /// <returns>The canonical secret, or null only when no credential exists.</returns>
    Task<string?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Atomically replaces the protected secret; callers independently read back the persisted value.</summary>
    /// <param name="secret">Canonical secret created by <see cref="McpAccessCredential.CreateSecret"/>.</param>
    /// <param name="cancellationToken">Cancels before durable replacement.</param>
    Task WriteAsync(string secret, CancellationToken cancellationToken);
}
