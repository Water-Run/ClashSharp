using ClashSharp.ApplicationModel.Agent;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Properties of the MCP bearer credential: generated secrets are canonical and unique, authentication succeeds for
/// exactly one value (every single-character mutation, case change, padding or truncation fails), and the derived
/// identity binds to the secret without revealing it.
/// </summary>
public sealed class McpAccessCredentialPropertyTests
{
    private const string HexAlphabet = "0123456789abcdef";

    /// <summary>Verifies generated secrets are canonical, unique and not degenerate.</summary>
    [Fact]
    public void CreateSecret_IsCanonicalUniqueAndWellDistributed()
    {
        HashSet<string> seen = [];
        int[] digitCounts = new int[16];
        for (int index = 0; index < 600; index++)
        {
            string secret = McpAccessCredential.CreateSecret();
            Assert.True(McpAccessCredential.IsCanonicalSecret(secret));
            Assert.True(seen.Add(secret), "a generated secret repeated");
            foreach (char digit in secret)
            {
                digitCounts[HexAlphabet.IndexOf(digit, StringComparison.Ordinal)]++;
            }
        }

        double expected = 600 * 64 / 16.0;
        Assert.All(digitCounts, count => Assert.InRange(count, expected * 0.85, expected * 1.15));
    }

    /// <summary>Verifies authentication accepts exactly the creation secret and rejects every neighbouring value.</summary>
    [Fact]
    public void Authentication_AcceptsExactlyOneSecret()
    {
        string secret = McpAccessCredential.CreateSecret();
        McpAccessCredential credential = new(secret);

        Assert.True(credential.TryAuthenticate(secret, out McpClientIdentity? identity));
        Assert.Equal(credential.Identity, identity);

        for (int position = 0; position < secret.Length; position++)
        {
            foreach (char replacement in HexAlphabet.Where(digit => digit != secret[position]))
            {
                string mutated = string.Concat(secret.AsSpan(0, position), replacement.ToString(), secret.AsSpan(position + 1));
                Assert.False(credential.TryAuthenticate(mutated, out McpClientIdentity? rejected));
                Assert.Null(rejected);
            }
        }

        string[] malformed =
        [
            secret.ToUpperInvariant(), secret + "0", secret[..63], " " + secret, secret + " ", secret + "\n", secret[..63] + "g", secret[..63] + "٣",
            string.Empty, " ", new string('0', 64), new string('f', 64), "Bearer " + secret, secret + secret,
        ];
        foreach (string candidate in malformed.Where(candidate => candidate != secret))
        {
            Assert.False(credential.TryAuthenticate(candidate, out _), candidate.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.False(credential.TryAuthenticate(null, out _));
    }

    /// <summary>Verifies the identity is a stable one-way function of the secret that never exposes it.</summary>
    [Fact]
    public void Identity_IsStableDistinctAndDoesNotRevealTheSecret()
    {
        HashSet<Guid> identities = [];
        for (int index = 0; index < 400; index++)
        {
            string secret = McpAccessCredential.CreateSecret();
            McpClientIdentity identity = new McpAccessCredential(secret).Identity;

            Assert.Equal(identity, new McpAccessCredential(secret).Identity);
            Assert.True(identities.Add(identity.CredentialId));
            Assert.NotEqual(Convert.ToHexStringLower(identity.CredentialId.ToByteArray()), secret[..32]);
            Assert.NotEqual(new Guid(Convert.FromHexString(secret[..32])), identity.CredentialId);
        }
    }

    /// <summary>Verifies non-canonical secrets are refused at construction instead of producing a weaker credential.</summary>
    [Fact]
    public void Construction_RefusesNonCanonicalSecrets()
    {
        string secret = McpAccessCredential.CreateSecret();
        foreach (string malformed in new[] { string.Empty, secret.ToUpperInvariant(), secret[..63], secret + "0", " " + secret[1..], secret[..63] + "x" })
        {
            Assert.Throws<ArgumentException>(() => new McpAccessCredential(malformed));
        }

        Assert.False(McpAccessCredential.IsCanonicalSecret(null));
        Assert.True(McpAccessCredential.IsCanonicalSecret(secret));
    }
}
