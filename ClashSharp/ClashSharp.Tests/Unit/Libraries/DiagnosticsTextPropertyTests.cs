using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Diagnostics;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Properties of diagnostic text handling: a credential value never survives persistence whatever its key, encoding,
/// serialization context or surroundings; normalization is bounded, idempotent and free of control characters; the
/// bounded text buffer never exceeds its budget under any append sequence.
/// </summary>
public sealed class DiagnosticsTextPropertyTests
{
    private static readonly string[] ConcreteCredentialNames =
    [
        "proxy-authorization", "authorization", "set-cookie", "cookie", "clientsecret", "client-secret", "client_secret",
        "accesstoken", "access-token", "access_token", "refreshtoken", "refresh-token", "idtoken", "id-token", "id_token",
        "api-key", "apikey", "api_key", "private-key", "privatekey", "private_key", "private-key-passphrase", "private_key_passphrase",
        "pre-shared-key", "preshared-key", "pre_shared_key", "shared-key", "primary-key", "ech-key", "psk", "auth", "auth-str",
        "auth_string", "authstring", "authentication", "obfs-password", "obfs_password", "password", "passwd", "pwd", "passphrase",
        "secret", "token", "uuid", "key",
    ];

    private static readonly string[] Templates =
    [
        "{K}={S}",
        "{K}: {S}",
        "{K} = {S}",
        "{{\"{K}\":\"{S}\"}}",
        "{{\"{K}\": \"{S}\", \"other\": 1}}",
        "'{K}': '{S}'",
        "{K}: \"{S} tail words\"",
        "[info] {K}={S}; retry=1",
        "note {K}: {S}\nnext line",
        "{{\"outer\":{{\"{K}\":[\"{S}\",\"{S}2\"]}}}}",
        "<{K}>{S}</{K}>",
        "{K}: Bearer {S}",
        "{K}:\n  - {S}\n  - {S}2\nafter",
        "{K}: |\n  {S}\n  {S}2\nafter",
        "{K}\n={S}",
        "{K}\r\n: {S}\nafter",
        "\"{K}\"\n:\n\"{S}\"",
    ];

    private static readonly string[] Prefixes = ["", "ERROR ", "2026-11-09T10:00:00Z [warn] ", "line1\nline2\n", "\t", "(ctx) ", "\r\n"];

    private static readonly string[] Suffixes = ["", " end", "\nnext", "\r\nnext", " ;", "  \t"];

    /// <summary>Verifies the concrete key list covers every alternative of the production credential-name pattern.</summary>
    [Fact]
    public void CredentialNameList_CoversEveryAlternativeOfTheProductionPattern()
    {
        string pattern = (string)typeof(PersistedLogText)
            .GetField("CredentialNames", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
        List<string> alternatives = [];
        int depth = 0;
        StringBuilder current = new();
        foreach (char character in pattern)
        {
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }

            if (character == '|' && depth == 0)
            {
                alternatives.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        alternatives.Add(current.ToString());
        Assert.True(alternatives.Count >= 20);
        foreach (string alternative in alternatives)
        {
            Regex matcher = new("^(?:" + alternative + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            Assert.Contains(ConcreteCredentialNames, name => matcher.IsMatch(name));
        }
    }

    /// <summary>Verifies a unique secret never survives for every key x serialization context x surrounding x casing.</summary>
    [Fact]
    public void Secret_NeverSurvivesAnyKeyContextOrSurrounding()
    {
        Random random = new(SettingsGenerators.Seed);
        int cases = 0;
        foreach (string key in ConcreteCredentialNames)
        {
            foreach (string template in Templates)
            {
                for (int variant = 0; variant < 4; variant++)
                {
                    string secret = NextSecret(random, variant);
                    string spelled = variant switch { 1 => key.ToUpperInvariant(), 2 => MixCase(key, random), _ => key };
                    string text = Prefixes[random.Next(Prefixes.Length)]
                        + template.Replace("{K}", spelled, StringComparison.Ordinal).Replace("{S}", secret, StringComparison.Ordinal).Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal)
                        + Suffixes[random.Next(Suffixes.Length)];

                    string persisted = PersistedLogText.Normalize(text);

                    AssertNoFragment(secret, persisted, $"key={spelled} template={template}");
                    Assert.Equal(persisted, PersistedLogText.Normalize(persisted));
                    cases++;
                }
            }
        }

        Assert.True(cases > 2000);
    }

    /// <summary>Verifies secrets inside URIs (userinfo, path, query, fragment) and private key blocks never survive.</summary>
    [Fact]
    public void Secret_NeverSurvivesInUrisOrPrivateKeyBlocks()
    {
        Random random = new(SettingsGenerators.Seed + 1);
        string[] schemes = ["http", "https", "socks5", "socks5h", "socks4", "socks4a", "ss", "ssr", "vmess", "vless", "trojan", "hysteria", "hysteria2", "hy2", "tuic", "HTTPS", "VMess"];
        foreach (string scheme in schemes)
        {
            for (int round = 0; round < 12; round++)
            {
                string secret = NextSecret(random, round);
                string[] uris =
                [
                    $"{scheme}://user:{secret}@host.example:443/p",
                    $"{scheme}://host.example/p?x=1&token={secret}&y=2#frag",
                    $"{scheme}://host.example/{secret}",
                    $"{scheme}://{secret}",
                    $"{scheme}://host.example/p#{secret}",
                ];
                foreach (string uri in uris)
                {
                    string text = "dial " + uri + " failed: refused";
                    string persisted = PersistedLogText.Normalize(text);
                    AssertNoFragment(secret, persisted, uri);
                    Assert.DoesNotContain("host.example", persisted, StringComparison.Ordinal);
                    Assert.Contains("failed: refused", persisted, StringComparison.Ordinal);
                }
            }
        }

        foreach (string label in new[] { "PRIVATE KEY", "RSA PRIVATE KEY", "EC PRIVATE KEY", "OPENSSH PRIVATE KEY", "ENCRYPTED PRIVATE KEY", "ECH KEYS", "PGP PRIVATE KEY BLOCK" })
        {
            for (int round = 0; round < 10; round++)
            {
                string secret = NextSecret(random, round);
                string body = $"{secret}\n{secret}2\r\n{secret}3";
                string block = $"-----BEGIN {label}-----\n{body}\n-----END {label}-----";
                string persisted = PersistedLogText.Normalize($"before {block} after");
                AssertNoFragment(secret, persisted, label);
                Assert.Contains("before", persisted, StringComparison.Ordinal);
                Assert.Contains("after", persisted, StringComparison.Ordinal);

                string unterminated = PersistedLogText.Normalize($"before -----BEGIN {label}-----\n{body}");
                AssertNoFragment(secret, unterminated, label + " (unterminated)");
                Assert.Contains("before", unterminated, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Verifies that after a credential key and separator no marker can survive whatever structure precedes or follows.</summary>
    [Fact]
    public void Marker_AfterACredentialKey_NeverSurvivesInArbitraryTokenSoup()
    {
        Random random = new(SettingsGenerators.Seed + 2);
        string[] tokens =
        [
            "alpha", "beta", "12345", " ", "\n", "\r\n", "\t", ",", ";", ":", "=", "\"", "'", "[", "]", "{", "}", "\\", "-----BEGIN PRIVATE KEY-----",
            "-----END PRIVATE KEY-----", "http://h/p?x=1", "C:\\Users\\bob", "<token>", "</token>", "<", "/>", "key", "\u202e", "\u0000", "é", "😀", "\ud800", "-", "_",
        ];
        string[] separators = ["=", ":", " = ", ": ", "\t=\t"];
        for (int round = 0; round < 6000; round++)
        {
            string marker = "ZZ" + random.Next(1_000_000, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture) + "ZZ";
            string key = ConcreteCredentialNames[random.Next(ConcreteCredentialNames.Length)];
            string text = Soup(random, tokens) + " " + key + separators[random.Next(separators.Length)] + marker + Soup(random, tokens);

            string persisted = PersistedLogText.Normalize(text);

            Assert.DoesNotContain(marker, persisted, StringComparison.Ordinal);
            Assert.True(persisted.Length <= RuntimeLogText.MaximumCharacters);
            AssertCleanText(persisted);
            string again = PersistedLogText.Normalize(persisted);
            Assert.True(persisted == again, $"not idempotent for input {Escape(text)} first {Escape(persisted)} second {Escape(again)}");
        }
    }

    /// <summary>Verifies benign diagnostics are preserved apart from control-character normalization.</summary>
    [Fact]
    public void BenignDiagnostics_AreNotOverRedacted()
    {
        Random random = new(SettingsGenerators.Seed + 3);
        string[] words = ["connection", "opened", "closed", "rule", "match", "proxy", "group", "selected", "latency", "ms", "bytes", "upload", "download", "dns", "resolved", "timeout", "retry", "started", "stopped", "profile", "loaded", "monkey", "tokenizer", "keyboard", "passwordless", "authority", "secretary"];
        for (int round = 0; round < 1500; round++)
        {
            StringBuilder builder = new();
            int count = 1 + random.Next(12);
            for (int index = 0; index < count; index++)
            {
                builder.Append(words[random.Next(words.Length)]);
                builder.Append(random.Next(5) switch { 0 => ": ", 1 => "=", 2 => ", ", 3 => " ", _ => "\n" });
                builder.Append(random.Next(1000));
                builder.Append(' ');
            }

            string text = builder.ToString().Trim();
            Assert.Equal(RuntimeLogText.Normalize(text), PersistedLogText.Normalize(text));
        }
    }

    /// <summary>Verifies profile directories are masked for every user name, separator and drive.</summary>
    [Fact]
    public void UserProfileNames_AreMaskedWhateverTheyContain()
    {
        string[] names = ["bob", "Bob Smith", "用户名", "o'brien", "name.with.dots", "x-y_z", "李 雷", "ÄÖÜ"];
        foreach (string name in names)
        {
            foreach (string drive in new[] { "C", "d", "Z" })
            {
                foreach (string separator in new[] { "\\", "/" })
                {
                    string text = $"open {drive}:{separator}Users{separator}{name}{separator}AppData{separator}Local failed";
                    string persisted = PersistedLogText.Normalize(text);
                    Assert.DoesNotContain(name, persisted, StringComparison.Ordinal);
                    Assert.Contains("%USERPROFILE%", persisted, StringComparison.Ordinal);
                    Assert.Contains("failed", persisted, StringComparison.Ordinal);
                }
            }
        }
    }

    /// <summary>Verifies adversarial inputs complete quickly, bounded and clean (no catastrophic regex behavior).</summary>
    [Fact]
    public void AdversarialInputs_CompleteQuicklyWithBoundedOutput()
    {
        string[] inputs =
        [
            "password:" + new string('[', 5000),
            "password:" + new string('{', 5000) + new string('}', 5000),
            string.Concat(Enumerable.Repeat("password:", 2000)),
            string.Concat(Enumerable.Repeat("\"", 8000)),
            "password: \"" + new string('\\', 8000),
            string.Concat(Enumerable.Repeat("-----BEGIN PRIVATE KEY-----", 300)),
            string.Concat(Enumerable.Repeat("<token>", 2000)),
            string.Concat(Enumerable.Repeat("http://", 2000)),
            new string('a', 1_000_000),
            string.Concat(Enumerable.Repeat("key=a ", 100_000)),
            string.Concat(Enumerable.Repeat("a\n  ", 100_000)) + "password:\n" + string.Concat(Enumerable.Repeat("  x\n", 50_000)),
            "😀" + new string('a', RuntimeLogText.MaximumCharacters - 2) + "😀😀",
        ];
        foreach (string input in inputs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            string persisted = PersistedLogText.Normalize(input);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"normalization of a {input.Length}-char input took {watch.Elapsed}");
            Assert.True(persisted.Length <= RuntimeLogText.MaximumCharacters);
            AssertCleanText(persisted);
        }
    }

    /// <summary>Verifies the runtime log normalizer is bounded, clean, idempotent and never produces lone surrogates.</summary>
    [Fact]
    public void RuntimeLogText_IsBoundedCleanAndIdempotentOverRandomText()
    {
        Random random = new(SettingsGenerators.Seed + 4);
        char[] alphabet = [.. "abcXYZ 019:=-_/\\\"'[]{}\t\r\n\u0000\u0007\u007f\u0085\u200e\u202e\u2066\u061c€é中😀", '\ud83d', '\ude00', '\ud800', '\udfff'];
        for (int round = 0; round < 3000; round++)
        {
            int length = random.Next(0, round % 50 == 0 ? 9000 : 300);
            string text = new([.. Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)])]);

            string normalized = RuntimeLogText.Normalize(text);

            Assert.True(normalized.Length <= RuntimeLogText.MaximumCharacters);
            AssertCleanText(normalized);
            Assert.Equal(normalized, RuntimeLogText.Normalize(normalized));
            Assert.Equal(normalized.Trim(), normalized);
        }

        string boundary = new string('a', RuntimeLogText.MaximumCharacters - 1) + "😀";
        string cut = RuntimeLogText.Normalize(boundary);
        AssertCleanText(cut);
        Assert.Equal(RuntimeLogText.MaximumCharacters - 1, cut.Length);
    }

    /// <summary>Verifies the bounded text buffer keeps its budget, ordering and single truncation marker under random appends.</summary>
    /// <param name="seedOffset">Offset added to the fixed seed.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BoundedTextBuffer_KeepsItsBudgetOrderAndSingleMarker(int seedOffset)
    {
        Random random = new(SettingsGenerators.Seed + seedOffset);
        for (int round = 0; round < 60; round++)
        {
            int maximum = ConcurrentBoundedTextBuffer.TruncationMarker.Length + Environment.NewLine.Length + random.Next(0, 400);
            ConcurrentBoundedTextBuffer buffer = new(maximum);
            List<string> accepted = [];
            for (int index = 0; index < 80; index++)
            {
                string line = random.Next(7) == 0 ? new string(' ', random.Next(3)) : new string((char)('a' + (index % 26)), random.Next(1, 120));
                bool added = buffer.TryAppendLine(line);
                if (added)
                {
                    accepted.Add(line);
                }
                else if (!string.IsNullOrWhiteSpace(line) && !buffer.IsTruncated)
                {
                    Assert.Fail("A non-blank line was refused without truncation.");
                }

                string snapshot = buffer.Snapshot();
                Assert.True(snapshot.Length <= maximum, $"snapshot {snapshot.Length} exceeds {maximum}");
                if (buffer.IsTruncated)
                {
                    Assert.EndsWith(ConcurrentBoundedTextBuffer.TruncationMarker + Environment.NewLine, snapshot, StringComparison.Ordinal);
                    Assert.Equal(1, Regex.Count(snapshot, Regex.Escape(ConcurrentBoundedTextBuffer.TruncationMarker)));
                }
            }

            string[] rendered = buffer.Snapshot().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            string[] kept = buffer.IsTruncated ? rendered[..^1] : rendered;
            Assert.Equal(accepted.Take(kept.Length), kept);
            buffer.Complete();
            Assert.False(buffer.TryAppendLine("late"));
            Assert.Throws<ArgumentException>(() => buffer.TryAppendLine("two\nlines"));
        }
    }

    /// <summary>Verifies concurrent appenders cannot exceed the budget or reorder one thread's lines.</summary>
    [Fact]
    public async Task BoundedTextBuffer_UnderConcurrentAppenders_PreservesPerThreadOrderAndBudget()
    {
        ConcurrentBoundedTextBuffer buffer = new(5000);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (int index = 0; index < 400; index++)
            {
                _ = buffer.TryAppendLine($"t{thread}-{index:D4}");
            }
        })));

        string snapshot = buffer.Snapshot();
        Assert.True(snapshot.Length <= 5000);
        Assert.True(buffer.IsTruncated);
        for (int thread = 0; thread < 8; thread++)
        {
            int[] indexes = [.. Regex.Matches(snapshot, $"t{thread}-(\\d{{4}})").Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
            Assert.Equal(indexes.Order(), indexes);
            Assert.Equal(indexes.Distinct().Count(), indexes.Length);
        }
    }

    private static string Soup(Random random, string[] tokens)
    {
        StringBuilder builder = new();
        int count = random.Next(0, 40);
        for (int index = 0; index < count; index++)
        {
            builder.Append(tokens[random.Next(tokens.Length)]);
        }

        return builder.ToString();
    }

    private static string MixCase(string value, Random random) =>
        new([.. value.Select(character => random.Next(2) == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character))]);

    private static string NextSecret(Random random, int variant)
    {
        const string alphanumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        string body = new([.. Enumerable.Range(0, 20).Select(_ => alphanumeric[random.Next(alphanumeric.Length)])]);
        return variant switch
        {
            1 => "Zq" + Convert.ToBase64String(Encoding.UTF8.GetBytes(body)).Replace("=", "A", StringComparison.Ordinal),
            2 => "Zq中文" + body[..8] + "密码" + body[8..],
            3 => "Zq" + body + "+/" + body[..4],
            _ => "Zq" + body,
        };
    }

    private static string Escape(string text) =>
        string.Concat(text.Select(character => character < ' ' || character > '~' ? $"\\u{(int)character:X4}" : character.ToString()));

    private static void AssertNoFragment(string secret, string persisted, string context)
    {
        const int window = 10;
        for (int start = 0; start + window <= secret.Length; start++)
        {
            string fragment = secret.Substring(start, window);
            if (fragment.All(static character => character <= 0x7F))
            {
                Assert.False(
                    persisted.Contains(fragment, StringComparison.Ordinal),
                    $"{context}: fragment '{fragment}' of the secret survived in '{persisted}'");
            }
        }

        foreach (string segment in Regex.Split(secret, "[\x00-\x7F]+").Where(static part => part.Length >= 2))
        {
            Assert.False(persisted.Contains(segment, StringComparison.Ordinal), $"{context}: segment '{segment}' of the secret survived in '{persisted}'");
        }
    }

    private static void AssertCleanText(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            Assert.False(char.IsControl(character), $"control character U+{(int)character:X4} survived");
            Assert.False(character is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'), "direction control survived");
            if (char.IsHighSurrogate(character))
            {
                Assert.True(index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]), "lone high surrogate");
                index++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(character), "lone low surrogate");
            }
        }
    }
}
