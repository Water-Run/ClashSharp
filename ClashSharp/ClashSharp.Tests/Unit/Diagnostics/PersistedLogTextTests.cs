using ClashSharp.Diagnostics;

namespace ClashSharp.Tests.Unit.Diagnostics;

public sealed class PersistedLogTextTests
{
    [Fact]
    public void Normalize_TruncatesBeforeAValidSurrogatePairInsteadOfReplacingHalfOfIt()
    {
        string prefix = new('x', RuntimeLogText.MaximumCharacters - 1);
        Assert.Equal(prefix, PersistedLogText.Normalize(prefix + "😀trailing text"));
    }

    [Theory]
    [InlineData("Authorization: Bearer private-access-token\noperation failed", "private-access-token")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
    [InlineData("""{"password":"private password", "uuid":"private-uuid"}""", "private password")]
    [InlineData("secret='controller-secret'; retry=true", "controller-secret")]
    [InlineData("Cookie: session=private-session; other=private-cookie", "private-session")]
    [InlineData("<Token>private-token</Token>", "private-token")]
    [InlineData("fetch https://user:private-password@example.test/path?token=private-token", "example.test")]
    [InlineData("node vmess://private-base64", "private-base64")]
    public void Normalize_RemovesKnownSecretsAndUris(string input, string secret)
    {
        string result = PersistedLogText.Normalize(input);
        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[redacted", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_PreservesNonSensitiveDiagnosticCodesAndMasksProfileNames()
    {
        string result = PersistedLogText.Normalize(
            """config.validation_failed at C:\Users\Private Name\AppData\config.yaml""");
        Assert.Equal(@"config.validation_failed at %USERPROFILE%\AppData\config.yaml", result);
    }

    [Fact]
    public void Normalize_BoundsOversizedValuesAndRemovesDirectionAndControlCharacters()
    {
        string input = "safe\u202E\u2066\0\ud800" + new string('x', 100_000);
        string result = PersistedLogText.Normalize(input);
        Assert.InRange(result.Length, 1, RuntimeLogText.MaximumCharacters);
        Assert.DoesNotContain('\u202E', result);
        Assert.DoesNotContain('\u2066', result);
        Assert.DoesNotContain('\0', result);
        Assert.DoesNotContain('\ud800', result);
    }

    [Fact]
    public void Normalize_CredentialRedactionIsStableAcrossRepeatedPersistence()
    {
        const string input = """secret="opaque"; token=abc; uri=https://example.test/?password=xyz""";
        string result = PersistedLogText.Normalize(input);
        Assert.Equal(result, PersistedLogText.Normalize(result));
        Assert.DoesNotContain("opaque", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", result, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private-key")]
    [InlineData("private_key")]
    [InlineData("privateKey")]
    [InlineData("private-key-passphrase")]
    [InlineData("pre-shared-key")]
    [InlineData("preshared-key")]
    [InlineData("pre_shared_key")]
    [InlineData("psk")]
    [InlineData("AUTH-STR")]
    [InlineData("auth_str")]
    [InlineData("authString")]
    [InlineData("auth")]
    [InlineData("authentication")]
    [InlineData("primary-key")]
    [InlineData("ech-key")]
    [InlineData("obfs-password")]
    [InlineData("key")]
    public void Normalize_ProtectsKnownProxyCredentialsAndRetainsFollowingDiagnosticLines(string key)
    {
        string result = PersistedLogText.Normalize($"config.rejected {key}: fixture-private-material\nnetwork.retry");
        Assert.DoesNotContain("fixture-private-material", result, StringComparison.Ordinal);
        Assert.Contains("config.rejected", result, StringComparison.Ordinal);
        Assert.Contains("network.retry", result, StringComparison.Ordinal);
        Assert.Contains("[redacted]", result, StringComparison.Ordinal);
        Assert.Equal(result, PersistedLogText.Normalize(result));
    }

    [Theory]
    [InlineData("password: \"prefix\\\"fixture-private-tail\"; retry.code")]
    [InlineData("password: 'prefix''fixture-private-tail'; retry.code")]
    [InlineData("token: \"prefix\\\\fixture-private-tail\"; retry.code")]
    [InlineData("secret: \"prefix\nfixture-private-tail\"; retry.code")]
    public void Normalize_RemovesEscapedAndMultilineQuotedCredentialSuffixes(string input)
    {
        string result = PersistedLogText.Normalize(input);
        Assert.DoesNotContain("fixture-private-tail", result, StringComparison.Ordinal);
        Assert.Contains("retry.code", result, StringComparison.Ordinal);
        Assert.Equal(result, PersistedLogText.Normalize(result));
    }

    [Theory]
    [InlineData("authentication: [\"a:fixture-first\", {nested: [\"fixture-second\"]}]\nnetwork.retry")]
    [InlineData("authentication: {one: \"fixture-first\", two: {value: \"fixture-second\"}}\nnetwork.retry")]
    [InlineData("private-key: |-\n  fixture-first\n  fixture-second\nnetwork.retry")]
    [InlineData("root:\n  private-key: >\n    fixture-first\n    fixture-second\n  status: network.retry")]
    [InlineData("authentication:\n  - a:fixture-first\n  - b:fixture-second\nnetwork.retry")]
    [InlineData("authentication:\n- a:fixture-first\n- b:fixture-second\nnetwork.retry")]
    [InlineData("password: fixture-first fixture-second\nnetwork.retry")]
    public void Normalize_RemovesCompleteStructuredAndContinuedCredentialValues(string input)
    {
        string result = PersistedLogText.Normalize(input);
        Assert.DoesNotContain("fixture-first", result, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-second", result, StringComparison.Ordinal);
        Assert.Contains("network.retry", result, StringComparison.Ordinal);
        Assert.Equal(result, PersistedLogText.Normalize(result));
    }

    [Theory]
    [InlineData("PRIVATE KEY")]
    [InlineData("RSA PRIVATE KEY")]
    [InlineData("OPENSSH PRIVATE KEY")]
    [InlineData("ENCRYPTED PRIVATE KEY")]
    [InlineData("ECH KEYS")]
    public void Normalize_RemovesPrivateKeyBlocksWithoutNeedingAFieldName(string label)
    {
        string result = PersistedLogText.Normalize($"parse.failed\n-----BEGIN {label}-----\nfixture-private-material\n-----END {label}-----\nnetwork.retry");
        Assert.DoesNotContain("fixture-private-material", result, StringComparison.Ordinal);
        Assert.DoesNotContain("-----BEGIN", result, StringComparison.Ordinal);
        Assert.Contains("parse.failed", result, StringComparison.Ordinal);
        Assert.Contains("network.retry", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_DoesNotExposeTailAfterMismatchedOrTruncatedPrivateKeyEnd()
    {
        string result = PersistedLogText.Normalize("parse.failed -----BEGIN OPENSSH PRIVATE KEY-----\nfixture-first\n-----END RSA PRIVATE KEY-----\nfixture-second");
        Assert.Equal("parse.failed [redacted]", result);
    }

    [Theory]
    [InlineData("<private-key><![CDATA[fixture-private-material]]></private-key>")]
    [InlineData("<authentication><password>fixture-first</password><user>fixture-private-material</user></authentication>")]
    public void Normalize_ConservativelyRemovesNestedXmlCredentialContent(string input)
    {
        string result = PersistedLogText.Normalize("parse.failed " + input);
        Assert.DoesNotContain("fixture-private-material", result, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-first", result, StringComparison.Ordinal);
        Assert.Contains("parse.failed", result, StringComparison.Ordinal);
        Assert.Equal(result, PersistedLogText.Normalize(result));
    }

    [Fact]
    public void Normalize_DistinguishesPublicAndHostKeysFromGenericSecretKeys()
    {
        const string input = "public-key: visible-public-material\nhost-key: visible-host-material\nprivate-key-identifier: visible-id\nkey: fixture-private-material";
        string result = PersistedLogText.Normalize(input);
        Assert.Contains("visible-public-material", result, StringComparison.Ordinal);
        Assert.Contains("visible-host-material", result, StringComparison.Ordinal);
        Assert.Contains("visible-id", result, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-private-material", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password: \"prefix\"fixture-private-tail")]
    [InlineData("authentication: [redacted]fixture-private-tail")]
    [InlineData("authentication: {wrong]fixture-private-tail")]
    public void Normalize_DropsMalformedCredentialTails(string input)
    {
        Assert.DoesNotContain("fixture-private-tail", PersistedLogText.Normalize(input), StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_BoundsExcessivelyNestedOrUnterminatedCredentials()
    {
        string nested = "authentication: " + new string('[', 160) + "fixture-private-material" + new string(']', 160);
        string quoted = "password: \"" + new string('x', 100_000);
        Assert.Equal("authentication: [redacted]", PersistedLogText.Normalize(nested));
        Assert.Equal("password: [redacted]", PersistedLogText.Normalize(quoted));
    }
}
