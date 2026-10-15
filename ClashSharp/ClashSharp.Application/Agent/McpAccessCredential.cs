using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Authenticates one high-entropy bearer secret while retaining only its hash in the listener.</summary>
public sealed class McpAccessCredential
{
    private readonly byte[] _hash;

    /// <summary>Creates the verifier from an independently persisted canonical secret.</summary>
    /// <param name="secret">Exactly 64 lowercase hexadecimal characters generated from 32 random bytes.</param>
    public McpAccessCredential(string secret)
    {
        if (!IsCanonicalSecret(secret)) { throw new ArgumentException("Invalid MCP credential shape.", nameof(secret)); }
        _hash = Hash(secret);
        Identity = new(new Guid(_hash.AsSpan(0, 16)));
    }

    /// <summary>Gets a detached identity suitable for comparing against the current credential after admission.</summary>
    public McpClientIdentity Identity { get; }

    /// <summary>Generates a new bearer secret; callers must keep the returned value out of logs and exported preferences.</summary>
    /// <returns>A fresh canonical 256-bit secret.</returns>
    public static string CreateSecret()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        try { return Convert.ToHexStringLower(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Checks the persisted and supplied credential shape without normalizing opaque token bytes.</summary>
    /// <param name="secret">Potential canonical bearer secret.</param>
    /// <returns>Whether the secret has the required fixed-size hexadecimal encoding.</returns>
    public static bool IsCanonicalSecret([NotNullWhen(true)] string? secret) => secret is { Length: 64 }
        && secret.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Compares the bearer secret in constant time after fixed-shape validation.</summary>
    /// <param name="secret">Secret supplied by an authenticated transport attempt.</param>
    /// <param name="identity">Verified non-secret identity, or null when authentication fails.</param>
    /// <returns>Whether the supplied credential matches this verifier.</returns>
    public bool TryAuthenticate(string? secret, [NotNullWhen(true)] out McpClientIdentity? identity)
    {
        identity = null;
        if (!IsCanonicalSecret(secret)) { return false; }
        byte[] candidate = Hash(secret);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(_hash, candidate)) { return false; }
            identity = Identity;
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    private static byte[] Hash(string secret)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(secret);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
