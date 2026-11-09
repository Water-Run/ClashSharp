using System.Reflection;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>
/// Properties of the managed runtime candidate built from an arbitrary imported profile: it never carries an
/// app-owned key from the profile, preserves everything else, is a fixed point of the builder, and the
/// transparent-proxy and ordinary candidates differ only where the design says they differ.
/// </summary>
public sealed class MihomoRuntimeCandidatePropertyTests
{
    private const int Seed = 20261109;
    private static readonly string Secret = new('a', 64);

    private static readonly ClashSharpMode[] Modes =
        [ClashSharpMode.Disabled, ClashSharpMode.Standby, ClashSharpMode.RuleTakeover, ClashSharpMode.FullTakeover];

    private static readonly HashSet<string> ManagedAlways =
    [
        "external-controller", "secret", "allow-lan", "bind-address", "mode", "mixed-port", "log-level",
    ];

    private static HashSet<string> OwnedKeys() =>
        (HashSet<string>)typeof(MihomoYamlSemanticValidator)
            .GetField("OwnedRuntimeKeys", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static YamlMappingNode Load(string text) => MihomoYamlSemanticValidator.LoadUniqueRootMappingNode(text, nameof(text));

    private static string Key(YamlNode node) => ((YamlScalarNode)node).Value ?? string.Empty;

    private static string Canonical(YamlNode node, Func<string, string>? scalarMap = null)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                return "s:" + (scalarMap?.Invoke(scalar.Value ?? string.Empty) ?? scalar.Value ?? string.Empty).Length + ":" + (scalar.Value ?? string.Empty);
            case YamlSequenceNode sequence:
                return "[" + string.Join(",", sequence.Children.Select(child => Canonical(child, scalarMap))) + "]";
            case YamlMappingNode mapping:
                return "{" + string.Join(",", mapping.Children
                    .Select(pair => Key(pair.Key) + "=>" + Canonical(pair.Value, scalarMap))
                    .Order(StringComparer.Ordinal)) + "}";
            default:
                throw new InvalidOperationException(node.GetType().Name);
        }
    }

    // ----------------------------------------------------------------- generator

    private static readonly string[] NestedKeys =
    [
        "name", "type", "server", "port", "mode", "secret", "listeners", "tun", "external-controller", "allow-lan", "log-level",
        "mixed-port", "tunnels", "authentication", "dns", "listen", "path", "url", "interval", "geox-url", "x", "y", "<", "a b", "香港",
    ];

    private static readonly string[] Scalars =
    [
        "1", "0", "true", "false", "null", "~", "'quoted'", "\"dq\"", "plain text", "'it''s'", "7890", "127.0.0.1:9090", "'0.0.0.0'", "'*'",
        "https://example.com/a?b=c", "😀", "'a,b'", "'[x'", "'#not-comment'", "1e3", "0x10", "2026-11-09",
    ];

    private static string FlowTree(Random random, int depth)
    {
        if (depth == 0 || random.Next(4) == 0)
        {
            return Scalars[random.Next(Scalars.Length)];
        }

        if (random.Next(2) == 0)
        {
            int count = random.Next(0, 4);
            return "[" + string.Join(", ", Enumerable.Range(0, count).Select(_ => FlowTree(random, depth - 1))) + "]";
        }

        int pairs = random.Next(0, 4);
        HashSet<string> used = [];
        List<string> entries = [];
        for (int index = 0; index < pairs; index++)
        {
            string key = NestedKeys[random.Next(NestedKeys.Length)];
            if (used.Add(key))
            {
                entries.Add("'" + key + "': " + FlowTree(random, depth - 1));
            }
        }

        return "{" + string.Join(", ", entries) + "}";
    }

    private static readonly string[] ForeignRoots =
    [
        "proxies", "proxy-groups", "rules", "rule-sets", "sniffer", "hosts", "experimental", "unified-delay", "tcp-concurrent", "ipv6",
        "keep-alive-interval", "find-process-mode", "global-client-fingerprint", "x-custom", "香港", "a b", "geodata-mode",
    ];

    private static string OwnedValue(Random random, string key)
    {
        string quoted = random.Next(3) switch { 0 => key, 1 => "\"" + key + "\"", _ => "'" + key + "'" };
        return random.Next(7) switch
        {
            0 => quoted + ": 1",
            1 => quoted + ": ''",
            2 => quoted + ": " + FlowTree(random, 2),
            3 => quoted + ":\n  - a\n  - b",
            4 => quoted + ":\n  k: v\n  n:\n    - 1",
            5 => quoted + ": &keep [1, 2]\nkept-alias: *keep",
            _ => quoted + ": null",
        };
    }

    private static string RandomProfile(Random random, HashSet<string> owned)
    {
        StringBuilder text = new();
        List<string> sections = [];
        int foreign = random.Next(1, 7);
        HashSet<string> usedRoots = [];
        for (int index = 0; index < foreign; index++)
        {
            string root = ForeignRoots[random.Next(ForeignRoots.Length)];
            if (usedRoots.Add(root))
            {
                sections.Add("'" + root + "': " + FlowTree(random, 3));
            }
        }

        string[] ownedList = [.. owned.Order(StringComparer.Ordinal)];
        int injected = random.Next(0, 8);
        HashSet<string> usedOwned = [];
        for (int index = 0; index < injected; index++)
        {
            string key = ownedList[random.Next(ownedList.Length)];
            if (usedOwned.Add(key))
            {
                sections.Insert(random.Next(sections.Count + 1), OwnedValue(random, key));
            }
        }

        foreach (string section in sections)
        {
            text.Append(section).Append('\n');
        }

        return text.ToString();
    }

    private static bool TryBuild(string profile, ClashSharpMode mode, bool tun, int port, out string result, out Exception? failure)
    {
        try
        {
            result = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(profile, port, mode, tun, Secret);
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or StableRuntimeDiagnosticException)
        {
            result = string.Empty;
            failure = exception;
            return false;
        }
    }

    // ----------------------------------------------------------------- properties

    [Fact]
    public void Candidate_IsAFixedPointNeverCarriesAnAppOwnedKeyAndKeepsEverythingElse()
    {
        HashSet<string> owned = OwnedKeys();
        Random random = new(Seed);
        int accepted = 0;
        int rejected = 0;
        for (int iteration = 0; iteration < 600; iteration++)
        {
            string profile = RandomProfile(random, owned);
            ClashSharpMode mode = Modes[random.Next(Modes.Length)];
            int port = random.Next(3) switch { 0 => 1, 1 => 65535, _ => random.Next(1, 65536) };
            foreach (bool tun in new[] { false, true })
            {
                if (!TryBuild(profile, mode, tun, port, out string candidate, out Exception? failure))
                {
                    // A refusal must be a typed, explainable one - never an arbitrary exception.
                    Assert.False(string.IsNullOrWhiteSpace(failure!.Message));
                    rejected++;
                    continue;
                }

                accepted++;
                string context = $"#{iteration} tun={tun} mode={mode}\n--- profile\n{profile}\n--- candidate\n{candidate}";
                MihomoYamlSemanticValidator.ValidateManagedRuntimeConfiguration(candidate, port, mode, tun, Secret);
                YamlMappingNode root = Load(candidate);
                Assert.DoesNotContain('\r', candidate);
                Assert.True(candidate.EndsWith('\n'), context);

                // 1. No app-owned key survives from the profile; only the managed keys exist.
                foreach (YamlNode key in root.Children.Keys)
                {
                    string name = Key(key);
                    if (owned.Contains(name))
                    {
                        Assert.True(ManagedAlways.Contains(name) || tun && name == "tun", $"{name} leaked\n{context}");
                    }
                }

                Assert.Equal(MihomoRuntimeConfigurationBuilder.MapToMihomoMode(mode), ((YamlScalarNode)root.Children[new YamlScalarNode("mode")]).Value);
                Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ((YamlScalarNode)root.Children[new YamlScalarNode("mixed-port")]).Value);
                Assert.Equal(tun, root.Children.ContainsKey(new YamlScalarNode("tun")));

                // 2. Everything the profile owns is preserved verbatim and in order.
                YamlMappingNode input = Load(profile);
                string[] expectedOrder = [.. input.Children.Keys.Select(Key).Where(name => !owned.Contains(name))];
                string[] actualOrder = [.. root.Children.Keys.Select(Key).Where(name => !owned.Contains(name) && expectedOrder.Contains(name))];
                Assert.Equal(expectedOrder, actualOrder);
                foreach (KeyValuePair<YamlNode, YamlNode> pair in input.Children.Where(pair => !owned.Contains(Key(pair.Key))))
                {
                    string name = Key(pair.Key);
                    if (name is "profile")
                    {
                        continue;
                    }

                    if (tun && name is "dns")
                    {
                        continue;
                    }

                    Assert.Equal(Canonical(pair.Value), Canonical(root.Children[new YamlScalarNode(name)]));
                }

                // 3. The candidate is a fixed point of the builder.
                Assert.True(TryBuild(candidate, mode, tun, port, out string again, out _), context);
                Assert.Equal(candidate, again);
                Assert.Equal(candidate, MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(profile, port, mode, tun, Secret));
            }
        }

        Assert.True(accepted > 400, $"only {accepted} accepted / {rejected} rejected");
    }

    [Fact]
    public void OrdinaryAndTransparentCandidatesDifferOnlyByTheTunSectionAndItsDnsDefaults()
    {
        HashSet<string> owned = OwnedKeys();
        Random random = new(Seed + 1);
        int compared = 0;
        for (int iteration = 0; iteration < 500; iteration++)
        {
            string profile = RandomProfile(random, owned);
            ClashSharpMode mode = Modes[random.Next(Modes.Length)];
            if (!TryBuild(profile, mode, false, 7890, out string ordinary, out _)
                || !TryBuild(profile, mode, true, 7890, out string transparent, out _))
            {
                continue;
            }

            YamlMappingNode left = Load(ordinary);
            YamlMappingNode right = Load(transparent);
            HashSet<string> leftKeys = [.. left.Children.Keys.Select(Key)];
            HashSet<string> rightKeys = [.. right.Children.Keys.Select(Key)];
            HashSet<string> addedByTransparent = [.. rightKeys.Except(leftKeys)];
            Assert.Subset(new HashSet<string> { "tun", "dns" }, addedByTransparent);
            Assert.Empty(leftKeys.Except(rightKeys));
            foreach (string name in leftKeys.Where(name => name != "dns"))
            {
                Assert.Equal(
                    Canonical(left.Children[new YamlScalarNode(name)]),
                    Canonical(right.Children[new YamlScalarNode(name)]));
            }

            // The ordinary candidate never contains a tun section; the transparent one always does.
            Assert.DoesNotContain("tun", leftKeys);
            Assert.Contains("tun", rightKeys);
            compared++;
        }

        Assert.True(compared > 200, compared.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EveryOwnedKeyIsRemovedFromTheProfileInEveryYamlSpellingAndCasesOfNestingKeepTheirOwnedNames()
    {
        foreach (string key in OwnedKeys().Order(StringComparer.Ordinal))
        {
            foreach (string spelling in new[] { key, "\"" + key + "\"", "'" + key + "'", "? " + key + "\n" })
            {
                string profile = $"{spelling}: leaked\nproxies:\n  - {{name: n, mode: global, secret: keep}}\nrules: []\n";
                foreach (bool tun in new[] { false, true })
                {
                    Assert.True(TryBuild(profile, ClashSharpMode.RuleTakeover, tun, 7890, out string candidate, out Exception? failure), $"{key}: {failure}");
                    YamlMappingNode root = Load(candidate);
                    string[] rootKeys = [.. root.Children.Keys.Select(Key)];
                    if (!ManagedAlways.Contains(key) && !(tun && key == "tun"))
                    {
                        Assert.DoesNotContain(key, rootKeys);
                    }

                    Assert.DoesNotContain("leaked", candidate, StringComparison.Ordinal);
                    // Nested look-alikes belong to a proxy and must survive.
                    Assert.Contains("secret: keep", candidate, StringComparison.Ordinal);
                    Assert.Contains("mode: global", candidate, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Candidate_EveryManagedValueComesFromTheCallerNeverFromTheProfile()
    {
        foreach (ClashSharpMode mode in Modes)
        {
            foreach (int port in new[] { 1, 80, 7890, 65535 })
            {
                const string Hostile = "mixed-port: 1\nport: 2\nsocks-port: 3\nredir-port: 4\ntproxy-port: 5\nallow-lan: true\nbind-address: '*'\nmode: global\nlog-level: debug\nexternal-controller: 0.0.0.0:9999\nexternal-controller-tls: 0.0.0.0:9998\nexternal-ui: /tmp\nsecret: ''\nlisteners: [{name: l, type: http, port: 1}]\ntun: {enable: true, stack: gvisor}\nauthentication: ['a:b']\nskip-auth-prefixes: [0.0.0.0/0]\nlan-allowed-ips: [0.0.0.0/0]\nproxies: []\nrules: []\n";
                foreach (bool tun in new[] { false, true })
                {
                    if (tun && mode is ClashSharpMode.Disabled or ClashSharpMode.Standby)
                    {
                        // The transparent proxy only exists for rule and global takeover; a direct-mode TUN candidate is refused.
                        Assert.Throws<StableRuntimeDiagnosticException>(
                            () => MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(Hostile, port, mode, tun, Secret));
                        continue;
                    }

                    string candidate = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(Hostile, port, mode, tun, Secret);
                    YamlMappingNode root = Load(candidate);
                    string Scalar(string key) => ((YamlScalarNode)root.Children[new YamlScalarNode(key)]).Value!;
                    Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture), Scalar("mixed-port"));
                    Assert.Equal(MihomoRuntimeConfigurationBuilder.MapToMihomoMode(mode), Scalar("mode"));
                    Assert.Equal("false", Scalar("allow-lan"));
                    Assert.Equal("127.0.0.1", Scalar("bind-address"));
                    Assert.Equal("info", Scalar("log-level"));
                    Assert.Equal(Secret, Scalar("secret"));
                    Assert.Equal(MihomoControllerEndpoint.ListenAddress, Scalar("external-controller"));
                    foreach (string forbidden in new[] { "port", "socks-port", "redir-port", "tproxy-port", "external-controller-tls", "external-ui", "listeners", "authentication", "skip-auth-prefixes", "lan-allowed-ips" })
                    {
                        Assert.False(root.Children.ContainsKey(new YamlScalarNode(forbidden)), forbidden);
                    }

                    if (tun)
                    {
                        YamlMappingNode tunNode = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("tun")]);
                        Assert.Equal("system", ((YamlScalarNode)tunNode.Children[new YamlScalarNode("stack")]).Value);
                    }
                }
            }
        }
    }

    // ----------------------------------------------------------------- hostile documents

    public static TheoryData<string> HostileDocumentNames() => [.. HostileDocuments().Select(static document => document.Name)];

    private static IEnumerable<(string Name, string Text, bool Accepted)> HostileDocuments()
    {
        string valid = "proxies: []\nrules: []\n";
        yield return ("empty", string.Empty, false);
        yield return ("whitespace only", "  \n\n \t\n", false);
        yield return ("comment only", "# nothing here\n", false);
        yield return ("two documents", valid + "---\n" + valid, false);
        yield return ("two empty documents", "---\n---\n", false);
        yield return ("root sequence", "- a\n- b\n", false);
        yield return ("root scalar", "just text\n", false);
        yield return ("root null", "~\n", false);
        yield return ("duplicate root key", "rules: []\nrules: []\n", false);
        yield return ("duplicate with different spelling", "rules: []\n\"rules\": []\n", false);
        yield return ("merge key", "base: &b {a: 1}\n<<: *b\nrules: []\n", false);
        yield return ("quoted merge key", "base: &b {a: 1}\n\"<<\": *b\nrules: []\n", false);
        yield return ("complex sequence key", "? [a, b]\n: c\nrules: []\n", false);
        yield return ("complex mapping key", "? {a: b}\n: c\nrules: []\n", false);
        yield return ("unknown alias", "x: *nowhere\nrules: []\n", false);
        yield return ("tab indentation", "proxies:\n\t- name: n\nrules: []\n", false);
        yield return ("unterminated quote", "x: 'abc\nrules: []\n", false);
        yield return ("unterminated flow", "x: [1, 2\nrules: []\n", false);
        yield return ("nul character", "x: a\0b\nrules: []\n", false);
        yield return ("bad indentation", "a:\n    b: 1\n  c: 2\nrules: []\n", false);
        yield return ("directive only", "%YAML 1.2\n", false);
        yield return ("deep flow", "x: " + new string('[', 100_000) + new string(']', 100_000) + "\nrules: []\n", false);
        yield return ("deep block", string.Concat(Enumerable.Range(0, 3000).Select(static index => new string(' ', index) + "a:\n")), false);
        yield return ("valid minimal", valid, true);
        yield return ("valid with crlf", "proxies: []\r\nrules: []\r\n", true);
        yield return ("valid with cr", "proxies: []\rrules: []\r", true);
        yield return ("valid with document markers", "---\nproxies: []\nrules: []\n...\n", true);
        yield return ("valid with comments", "# c\nproxies: []  # trailing\n\n\n# c\nrules: []\n", true);
        yield return ("valid with non-string scalar keys", "1: one\ntrue: yes\n~: nothing\nrules: []\n", true);
        yield return ("valid single quoted owned key", "'secret': x\nrules: []\n", true);
        yield return ("valid explicit key", "? rules\n: []\n", true);
        yield return ("valid block scalars", "x: |\n  line one\n\n  line two\ny: >-\n  folded\n  text\nrules: []\n", true);
        yield return ("valid anchors and aliases", "a: &a [1, 2]\nb: *a\nc: &c {k: *a}\nrules: []\n", true);
        yield return ("valid unicode", "香港: 节点\n😀: ✓\nrules: []\n", true);
        yield return ("valid huge scalar", "x: '" + new string('y', 3_000_000) + "'\nrules: []\n", true);
        yield return ("valid many keys", string.Concat(Enumerable.Range(0, 60_000).Select(static index => "k" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n")), true);
        yield return ("valid empty values", "a:\nb: ''\nc: ~\nrules: []\n", true);
        yield return ("valid long flow", "x: [" + string.Join(",", Enumerable.Range(0, 100_000)) + "]\nrules: []\n", true);
        yield return ("valid nesting at the limit", "x: " + new string('[', MihomoYamlSemanticValidator.MaxNestingDepth - 1) + new string(']', MihomoYamlSemanticValidator.MaxNestingDepth - 1) + "\nrules: []\n", true);
        yield return ("nesting beyond the limit", "x: " + new string('[', MihomoYamlSemanticValidator.MaxNestingDepth) + new string(']', MihomoYamlSemanticValidator.MaxNestingDepth) + "\nrules: []\n", false);
    }

    [Theory]
    [MemberData(nameof(HostileDocumentNames))]
    public void HostileDocument_IsAcceptedOrRefusedWithATypedErrorAndAcceptedOnesStayValid(string name)
    {
        (string _, string text, bool accepted) = HostileDocuments().Single(document => document.Name == name);

        foreach (bool tun in new[] { false, true })
        {
            if (accepted)
            {
                string candidate;
                try
                {
                    candidate = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(text, 7890, ClashSharpMode.RuleTakeover, tun, Secret);
                }
                catch (StableRuntimeDiagnosticException) when (tun)
                {
                    // The privileged service has structural limits; a valid but oversized profile is refused for TUN with a stable code.
                    continue;
                }

                MihomoYamlSemanticValidator.ValidateManagedRuntimeConfiguration(candidate, 7890, ClashSharpMode.RuleTakeover, tun, Secret);
                Assert.Equal(candidate, MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(candidate, 7890, ClashSharpMode.RuleTakeover, tun, Secret));
            }
            else
            {
                Assert.Throws<ArgumentException>(() => MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(text, 7890, ClashSharpMode.RuleTakeover, tun, Secret));
            }
        }

        Assert.Equal(accepted, TryValidate(text));
    }

    private static bool TryValidate(string text)
    {
        try
        {
            MihomoYamlSemanticValidator.ValidateOverlayInput(text);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void Mutations_OfAValidProfileNeverEscapeTheTypedFailureSet()
    {
        string baseProfile = "mixed-port: 7890\nproxies:\n  - {name: 'a b', type: ss, server: 1.2.3.4, port: 1}\n  - name: c\n    type: vmess\nproxy-groups:\n  - name: g\n    type: select\n    proxies: [a b, c]\nrules:\n  - 'DOMAIN,x.com,g'\n  - MATCH,DIRECT\nanchors: &z [1, 2]\nref: *z\n";
        string[] inserts = ["\t", "\0", "'", "\"", ": ", "- ", "{", "}", "[", "]", ",", "&", "*", "<<", "---", "...", "\r", "\uFEFF", "#", "%", "!", "?", "|", ">", "  ", "\n", "\n  ", "mode: x\n", "secret: y\n", "tun:\n  enable: true\n"];
        Random random = new(Seed + 2);
        int ok = 0;
        for (int iteration = 0; iteration < 4000; iteration++)
        {
            StringBuilder text = new(baseProfile);
            int edits = random.Next(1, 4);
            for (int edit = 0; edit < edits; edit++)
            {
                int position = random.Next(0, text.Length + 1);
                switch (random.Next(3))
                {
                    case 0:
                        text.Insert(position, inserts[random.Next(inserts.Length)]);
                        break;
                    case 1:
                        if (position < text.Length)
                        {
                            text.Remove(position, Math.Min(random.Next(1, 8), text.Length - position));
                        }

                        break;
                    default:
                        if (position < text.Length)
                        {
                            text[position] = (char)random.Next(0x20, 0x7f);
                        }

                        break;
                }
            }

            string profile = text.ToString();
            foreach (bool tun in new[] { false, true })
            {
                if (TryBuild(profile, ClashSharpMode.RuleTakeover, tun, 7890, out string candidate, out _))
                {
                    MihomoYamlSemanticValidator.ValidateManagedRuntimeConfiguration(candidate, 7890, ClashSharpMode.RuleTakeover, tun, Secret);
                    ok++;
                }
            }
        }

        Assert.True(ok > 100);
    }

    // ----------------------------------------------------------------- normalization

    [Fact]
    public void Normalize_IsIdempotentLfOnlyAndEndsWithExactlyOneMoreLineBreakThanNeeded()
    {
        string[] atoms = ["a", "b: c", " ", "\r\n", "\r", "\n", "\uFEFF", "\t", "香港", "\u2028", "\0", "|\n  x\n\n"];
        Random random = new(Seed + 3);
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            StringBuilder text = new();
            int count = random.Next(0, 12);
            for (int index = 0; index < count; index++)
            {
                text.Append(atoms[random.Next(atoms.Length)]);
            }

            string once = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(text.ToString());
            string twice = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(once);
            Assert.True(once == twice, $"#{iteration} not idempotent for {text.ToString().Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\uFEFF", "<BOM>", StringComparison.Ordinal)}");
            Assert.DoesNotContain('\r', once);
            Assert.True(once.EndsWith('\n'));
            Assert.False(once.StartsWith('\uFEFF'));
        }

        Assert.Equal("a\n", MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText("a"));
        Assert.Equal("a\n", MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText("a\n"));
        Assert.Equal("a\n\n", MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText("a\n\n"));
        Assert.Equal("a\nb\nc\n", MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText("a\r\nb\rc"));
    }
}
