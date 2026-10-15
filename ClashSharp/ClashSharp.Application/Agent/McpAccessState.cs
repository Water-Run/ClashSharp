using System.Diagnostics.CodeAnalysis;
using ClashSharp.Agent;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Publishes the effective access policy and credential as one immutable authentication snapshot.</summary>
public sealed class McpAccessState
{
    private AccessSnapshot _current = new(McpAccessPolicy.Default, null);

    /// <summary>Gets the effective local policy without I/O.</summary>
    public McpAccessPolicy Policy => Volatile.Read(ref _current).Policy;

    /// <summary>Gets whether an independently verified credential is available.</summary>
    public bool HasCredential => Volatile.Read(ref _current).Credential is not null;

    internal McpAccessCredential? Credential => Volatile.Read(ref _current).Credential;

    internal void Publish(McpAccessPolicy policy, McpAccessCredential? credential) =>
        Volatile.Write(ref _current, new(policy, credential));

    /// <summary>Authenticates against the current enabled snapshot without retaining request secrets.</summary>
    /// <param name="secret">Untrusted bearer secret.</param>
    /// <param name="identity">Verified identity, or null when access is disabled or the credential differs.</param>
    /// <returns>Whether current authentication succeeds.</returns>
    public bool TryAuthenticate(string secret, [NotNullWhen(true)] out McpClientIdentity? identity)
    {
        AccessSnapshot snapshot = Volatile.Read(ref _current);
        identity = null;
        return snapshot.Policy.Enabled && snapshot.Credential is not null
            && snapshot.Credential.TryAuthenticate(secret, out identity);
    }

    /// <summary>Rejects identities whose credential has been replaced or whose endpoint has been disabled.</summary>
    /// <param name="identity">Previously authenticated identity.</param>
    /// <returns>Whether the identity belongs to the current enabled snapshot.</returns>
    public bool IsCurrentIdentity(McpClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        AccessSnapshot snapshot = Volatile.Read(ref _current);
        return snapshot.Policy.Enabled && snapshot.Credential?.Identity == identity;
    }

    private sealed record AccessSnapshot(McpAccessPolicy Policy, McpAccessCredential? Credential);
}
