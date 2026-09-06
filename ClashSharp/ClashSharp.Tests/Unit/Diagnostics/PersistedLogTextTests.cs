using ClashSharp.Diagnostics;

namespace ClashSharp.Tests.Unit.Diagnostics;

public sealed class PersistedLogTextTests
{
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
}
