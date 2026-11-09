using System.Text;
using ClashSharp.Model;
using ClashSharp.Model.Rules;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>
/// Metamorphic and totality contracts of the profile preview: the meaning of a profile does not depend on how
/// its YAML is written (block or flow, quoting, line endings, comments, indentation) and no text can make the
/// preview throw.
/// </summary>
public sealed class MihomoProfilePreviewContractTests
{
    private const int Seed = 20261109;

    private static RegionMetadata Resolve(string code) => new(code, code, code);

    private static string Source(string key) => "<" + key + ">";

    private static IEnumerable<RenderOptions> AllRenderings(Random random, bool singleQuotesOnly)
    {
        foreach (YamlStyle style in Enum.GetValues<YamlStyle>())
        {
            foreach (LineEnding ending in Enum.GetValues<LineEnding>())
            {
                yield return new RenderOptions(
                    style, ending, Bom: false, Comments: random.Next(2) == 0, Indent: random.Next(1, 5), random.Next(0, 1000),
                    SingleQuotesOnly: singleQuotesOnly);
            }
        }
    }

    private static List<ProxyNode> ExpectedNodes(ProfileModel model)
    {
        List<ProxyNode> nodes = [];
        foreach (NodeSpec node in model.Nodes.Where(static node => !string.IsNullOrWhiteSpace(node.Name)))
        {
            string host = node.Server ?? string.Empty;
            string region = RegionCatalog.Infer(node.Name);
            if (region == "UN")
            {
                region = RegionCatalog.Infer(host);
            }

            nodes.Add(new ProxyNode(node.Name, MihomoProfileModelGenerator.ExpectedProtocol(node.Type), Resolve(region), null, host,
                MihomoProfileModelGenerator.ExpectedPort(node.Port)));
        }

        foreach (ProviderSpec provider in model.Providers)
        {
            string host = MihomoProfileModelGenerator.ExpectedHostFromUrl(provider.Url);
            string region = RegionCatalog.Infer(provider.Name);
            if (region == "UN")
            {
                region = RegionCatalog.Infer(host);
            }

            nodes.Add(new ProxyNode(
                provider.Name,
                "PROVIDER/" + (string.IsNullOrEmpty(provider.Type) ? "PROVIDER" : provider.Type.ToUpperInvariant()),
                Resolve(region),
                null,
                host));
        }

        return nodes;
    }

    private static List<RulePreview> ExpectedRules(ProfileModel model) =>
    [
        .. model.Rules.Select(static raw => MihomoRuleSyntax.TryParse(raw, out ParsedMihomoRule rule)
            ? new RulePreview(Source("ProfilePreview.CurrentConfiguration"), rule.Type, rule.Payload, rule.Action, 0)
            : new RulePreview(Source("ProfilePreview.CurrentConfiguration"), raw, string.Empty, string.Empty, 0) { IsHitCountKnown = false }),
    ];

    private static void AssertSameNodes(IReadOnlyList<ProxyNode> expected, IReadOnlyList<ProxyNode> actual, string context)
    {
        Assert.True(expected.Count == actual.Count, $"{context}: expected {expected.Count} rows, got {actual.Count}");
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.True(expected[index] == actual[index], $"{context}: row {index}: expected {expected[index]}, got {actual[index]}");
        }
    }

    [Fact]
    public void Nodes_EveryRenderingOfAModelYieldsTheModelsRows()
    {
        Random random = new(Seed);
        int models = 0;
        for (int iteration = 0; iteration < 250; iteration++)
        {
            ProfileModel model = MihomoProfileModelGenerator.RandomModel(random, safeAlphabet: false);
            List<ProxyNode> expected = ExpectedNodes(model);
            foreach (RenderOptions options in AllRenderings(random, singleQuotesOnly: false))
            {
                string text = MihomoProfileModelGenerator.Render(model, options, random);
                IReadOnlyList<ProxyNode> actual = MihomoProfilePreviewParser.ParseNodes(text, Resolve);
                AssertSameNodes(expected, actual, $"#{iteration} {options.Style}/{options.Ending}\n{text}");
            }

            models++;
        }

        Assert.Equal(250, models);
    }

    [Fact]
    public void Rules_EveryRenderingOfAModelYieldsTheModelsRulesInOrder()
    {
        Random random = new(Seed + 1);
        for (int iteration = 0; iteration < 250; iteration++)
        {
            ProfileModel model = MihomoProfileModelGenerator.RandomModel(random, safeAlphabet: false);
            List<RulePreview> expected = ExpectedRules(model);
            foreach (RenderOptions options in AllRenderings(random, singleQuotesOnly: false))
            {
                string text = MihomoProfileModelGenerator.Render(model, options, random);
                IReadOnlyList<RulePreview> actual = MihomoProfilePreviewParser.ParseRules(text, Source);
                Assert.True(expected.SequenceEqual(actual), $"#{iteration} {options.Style}/{options.Ending}\n{text}");
            }
        }
    }

    [Fact]
    public void PreviewDoesNotDependOnTheFallbackScannerForSemanticallyAmbiguousButReadableDocuments()
    {
        // A duplicate unrelated root key makes the document ambiguous for the semantic loader; the line scanner takes over
        // and must still recover the same rows for ordinary documents.
        const string Poison = "dns:\n  enable: true\ndns:\n  enable: false\n";
        Random random = new(Seed + 2);
        for (int iteration = 0; iteration < 250; iteration++)
        {
            ProfileModel model = MihomoProfileModelGenerator.RandomModel(random, safeAlphabet: true);
            List<ProxyNode> expectedNodes = ExpectedNodes(model);
            List<RulePreview> expectedRules = ExpectedRules(model);
            foreach (RenderOptions options in AllRenderings(random, singleQuotesOnly: true))
            {
                string text = MihomoProfileModelGenerator.Render(model, options with { Poison = Poison }, random);
                Assert.Throws<ArgumentException>(() => MihomoYamlSemanticValidator.LoadUniqueRootMappingNode(text, "text"));
                AssertSameNodes(expectedNodes, MihomoProfilePreviewParser.ParseNodes(text, Resolve), $"#{iteration} {options.Style}/{options.Ending}\n{text}");
                Assert.True(
                    expectedRules.SequenceEqual(MihomoProfilePreviewParser.ParseRules(text, Source)),
                    $"#{iteration} rules {options.Style}/{options.Ending}\n{text}");
            }
        }
    }

    [Fact]
    public void Preview_IsTotalForRandomYamlLikeText()
    {
        string[] tokens =
        [
            "proxies:", "proxy-providers:", "rules:", "proxy-groups:", "- ", "  ", "\t", "\n", "\r\n", "\r", ": ", ":", "{", "}", "[", "]",
            ",", "'", "\"", "#", " #", "name", "type", "server", "port", "url", "ss", "http", "香港", "😀", "\u0000", "\uFEFF", "&a", "*a",
            "<<", "?", "!!str", "|", ">", "---", "...", "%", "@", "`", "\\", "-", "--", "1", "65535", "99999999999", "x", " ", "no-resolve",
        ];
        Random random = new(Seed + 3);
        for (int iteration = 0; iteration < 6000; iteration++)
        {
            StringBuilder text = new();
            int count = random.Next(0, 80);
            for (int index = 0; index < count; index++)
            {
                text.Append(tokens[random.Next(tokens.Length)]);
            }

            string input = text.ToString();
            IReadOnlyList<ProxyNode> nodes = MihomoProfilePreviewParser.ParseNodes(input, Resolve);
            IReadOnlyList<RulePreview> rules = MihomoProfilePreviewParser.ParseRules(input, Source);
            Assert.All(nodes, static node => Assert.False(string.IsNullOrWhiteSpace(node.Name)));
            Assert.All(nodes, static node => Assert.True(node.ServerPort is null or (>= 1 and <= 65535)));
            Assert.All(rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.RuleType)));
        }
    }

    [Fact]
    public void Preview_SurvivesHostileShapesWithoutThrowingOrOverflowingTheStack()
    {
        string[] documents =
        [
            "proxies: " + new string('[', 200_000) + new string(']', 200_000) + "\nrules: []\n",
            string.Concat(Enumerable.Range(0, 3000).Select(static index => new string(' ', index) + "a:\n")),
            "proxies:\n" + string.Concat(Enumerable.Repeat("  - {name: n, type: ss, server: h, port: 1}\n", 50_000)),
            "rules:\n" + string.Concat(Enumerable.Repeat("  - DOMAIN,example.com,PROXY\n", 80_000)),
            "proxies:\n  - name: " + new string('x', 2_000_000) + "\n",
            "rules:\n  - AND," + string.Concat(Enumerable.Repeat("(", 100_000)) + "\n",
            "x: &a [1]\ny: &b [*a,*a,*a,*a]\nz: &c [*b,*b,*b,*b]\nrules: [*c]\n",
            "\0\0\0",
            string.Empty,
            "   \n\n  ",
            "# only a comment",
            "a: b: c: d",
            "- - - - - -",
            "? [complex, key]\n: value\nproxies:\n  - {name: n}\n",
            "proxies:\n\t- name: tab-indented\n",
            "proxies:\n  - name: dup\n    name: dup2\n",
        ];

        foreach (string document in documents)
        {
            _ = MihomoProfilePreviewParser.ParseNodes(document, Resolve);
            _ = MihomoProfilePreviewParser.ParseRules(document, Source);
        }

        Assert.Equal(50_000, MihomoProfilePreviewParser.ParseNodes(documents[2], Resolve).Count);
        Assert.Equal(80_000, MihomoProfilePreviewParser.ParseRules(documents[3], Source).Count);
    }

    [Fact]
    public void Nodes_DuplicateKeysTabsAndNonStringKeysFallBackWithoutLosingTheProxies()
    {
        (string Name, string Text)[] documents =
        [
            ("duplicate root key", "proxies:\n  - {name: A, type: ss, server: h, port: 1}\nrules: []\nrules: []\n"),
            ("merge key", "base: &b {udp: true}\nproxies:\n  - name: A\n    type: ss\n    server: h\n    port: 1\n    <<: *b\n"),
            ("complex key", "? [a, b]\n: c\nproxies:\n  - {name: A, type: ss, server: h, port: 1}\n"),
            ("two documents", "proxies:\n  - {name: A, type: ss, server: h, port: 1}\n---\nproxies: []\n"),
        ];
        foreach ((string name, string text) in documents)
        {
            IReadOnlyList<ProxyNode> nodes = MihomoProfilePreviewParser.ParseNodes(text, Resolve);
            Assert.True(nodes.Any(static node => node.Name == "A"), name);
        }
    }

    // --------------------------------------------------------------------- semantics that must not drift

    [Theory]
    [InlineData("HK-01 premium", "HK")]
    [InlineData("香港 IEPL 01", "HK")]
    [InlineData("🇯🇵 Tokyo 2", "JP")]
    [InlineData("US Los Angeles", "US")]
    [InlineData("新加坡 BGP", "SG")]
    [InlineData("台湾 Hinet", "TW")]
    [InlineData("Singapore-3", "SG")]
    public void Region_NameHintsWinOverTheServerHost(string name, string expected)
    {
        string text = $"proxies:\n  - {{name: '{name}', type: ss, server: example.de, port: 443}}\n";

        ProxyNode node = Assert.Single(MihomoProfilePreviewParser.ParseNodes(text, Resolve));

        Assert.Equal(expected, node.Region.RegionCode);
    }

    [Fact]
    public void Region_InferenceAlwaysYieldsAPackagedCodeOrTheUnknownMarker()
    {
        Random random = new(Seed + 4);
        for (int iteration = 0; iteration < 4000; iteration++)
        {
            string name = MihomoProfileModelGenerator.RandomName(random);
            string code = RegionCatalog.Infer(name);
            Assert.True(code == "UN" || RegionCatalog.Contains(code), $"'{name}' -> '{code}'");
            Assert.Equal(code, RegionCatalog.Infer(name));
        }

        Assert.Equal("UN", RegionCatalog.Infer(string.Empty));
    }

    [Fact]
    public void Region_EveryPackagedCodeIsItsOwnPrefixHintAndEveryLocalizedNameResolvesToACodeOfTheSameFlag()
    {
        int resolved = 0;
        foreach (string code in RegionCatalog.Codes)
        {
            foreach (AppLanguage language in Enum.GetValues<AppLanguage>().Where(static language => (int)language is >= 0 and <= 7))
            {
                string name = RegionCatalog.GetDisplayName(code, language);
                Assert.False(string.IsNullOrWhiteSpace(name), $"{code}/{language}");
                string inferred = RegionCatalog.Infer(name);
                Assert.True(inferred == "UN" || RegionCatalog.Contains(inferred), $"{code}/{language}: '{name}' -> '{inferred}'");
                if (inferred == code)
                {
                    resolved++;
                }
            }
        }

        // The localized names are the primary hint source: a clear majority must resolve back to their own region.
        Assert.True(resolved > RegionCatalog.Codes.Count * 4, $"only {resolved} localized names resolved back");
    }

    [Fact]
    public void Nodes_EveryNodeKindKeepsItsNormalizedProtocolAndAMissingKindIsUnknown()
    {
        foreach (string kind in MihomoProfileModelGenerator.NodeTypes)
        {
            string text = $"proxies:\n  - {{name: n, type: {kind}, server: h, port: 1}}\n";
            Assert.Equal(kind.ToUpperInvariant(), Assert.Single(MihomoProfilePreviewParser.ParseNodes(text, Resolve)).Protocol);
        }

        Assert.Equal("UNKNOWN", Assert.Single(MihomoProfilePreviewParser.ParseNodes("proxies:\n  - {name: n, server: h}\n", Resolve)).Protocol);
        Assert.Equal("UNKNOWN", Assert.Single(MihomoProfilePreviewParser.ParseNodes("proxies:\n  - {name: n, type: '', server: h}\n", Resolve)).Protocol);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("443", 443)]
    [InlineData("65535", 65535)]
    [InlineData("0", null)]
    [InlineData("65536", null)]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    [InlineData("99999999999", null)]
    [InlineData("", null)]
    public void Nodes_PortIsKeptOnlyInsideTheTcpRange(string port, int? expected)
    {
        string text = $"proxies:\n  - {{name: n, type: ss, server: h, port: '{port}'}}\n";

        Assert.Equal(expected, Assert.Single(MihomoProfilePreviewParser.ParseNodes(text, Resolve)).ServerPort);
    }

    [Fact]
    public void Nodes_NamelessEntriesAreSkippedAndDuplicatesAreKept()
    {
        const string Text = "proxies:\n  - {type: ss, server: h}\n  - {name: '', type: ss}\n  - {name: '   ', type: ss}\n  - {name: A, type: ss}\n  - {name: A, type: ss}\n  - plain-string\n  - [a, b]\n";

        IReadOnlyList<ProxyNode> nodes = MihomoProfilePreviewParser.ParseNodes(Text, Resolve);

        Assert.Equal(["A", "A"], nodes.Select(static node => node.Name).ToArray());
    }
}
