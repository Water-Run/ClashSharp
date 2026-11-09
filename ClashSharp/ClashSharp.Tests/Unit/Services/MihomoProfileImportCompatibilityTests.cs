using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Strings;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Tests.Unit.Services;

public sealed class MihomoProfileImportCompatibilityTests
{
    [Theory]
    [InlineData("../outside.yaml")]
    [InlineData("cache/../outside.yaml")]
    [InlineData("C:/outside.yaml")]
    public void ServicePolicy_PreflightsUnsafeProviderPaths(string path)
    {
        string source = $"rules: ['MATCH,DIRECT']\nproxy-providers: {{nodes: {{type: http, url: 'https://example.invalid/nodes', path: '{path}'}}}}\n";
        StableRuntimeDiagnosticException failure = Assert.Throws<StableRuntimeDiagnosticException>(() =>
            MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(source, 7890, ClashSharpMode.RuleTakeover, true, new string('a', 64)));
        Assert.Equal(RuntimeFailureDiagnostics.TunPolicyUnsupported, failure.DiagnosticCode);
    }

    private static string Candidate(string source, bool transparentProxy) =>
        MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(source, 7890, ClashSharpMode.RuleTakeover, transparentProxy, new string('a', 64));

    /// <summary>The service rejects a document by key name wherever the key sits, so the keys are enumerated from it.</summary>
    public static TheoryData<string, string> ServiceForbiddenKeys()
    {
        TheoryData<string, string> data = [];
        foreach (string set in new[]
                 {
                     "ForbiddenServiceRootKeys", "ForbiddenExternalUiKeys", "ForbiddenControllerKeys", "ForbiddenLocalCredentialKeys",
                 })
        {
            System.Reflection.FieldInfo field = typeof(ClashSharp.MihomoService.MihomoServiceConfigurationTrustValidator)
                .GetField(set, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            foreach (string key in ((IEnumerable<string>)field.GetValue(null)!).Order(StringComparer.Ordinal))
            {
                data.Add(set, key);
            }
        }

        return data;
    }

    /// <summary>
    /// The contract between the application and the privileged service: for every key the service forbids, the
    /// candidate the application hands it either is refused up front with the stable diagnostic or does not contain
    /// the key at all. It is never an unexpected failure and never a candidate the service would reject later.
    /// </summary>
    [Theory]
    [MemberData(nameof(ServiceForbiddenKeys))]
    public void TransparentProxyCandidate_NeverCarriesAKeyTheServiceForbids(string set, string key)
    {
        string source = "rules: ['MATCH,DIRECT']\n" + key + ": value\n";

        string? candidate = null;
        StableRuntimeDiagnosticException? refusal = null;
        try { candidate = Candidate(source, transparentProxy: true); }
        catch (StableRuntimeDiagnosticException failure) { refusal = failure; }

        if (refusal is not null)
        {
            Assert.True(
                refusal.DiagnosticCode == RuntimeFailureDiagnostics.TunPolicyUnsupported,
                $"{set}.{key} was refused with '{refusal.DiagnosticCode}' instead of the stable policy diagnostic.");
        }
        else
        {
            bool carried = Flatten(LoadRoot(candidate!)).Any(pair => pair.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.Ordinal));
            Assert.True(!carried, $"{set}.{key} reached the service candidate.");
        }
    }

    /// <summary>
    /// A profile preference whose privileged effect the service must not have is switched off, never a reason to
    /// refuse the profile: such fields are routine in subscriptions and would otherwise disable the default
    /// transparent proxy. The ordinary-mode candidate keeps the profile as written.
    /// </summary>
    [Theory]
    [InlineData("geox-url: {geoip: 'https://example.invalid/geoip.dat'}", "geox-url")]
    [InlineData("geo-auto-update: true", "geo-auto-update")]
    [InlineData("geo-auto-update: 'yes'", "geo-auto-update")]
    [InlineData("geo-auto-update: 1", "geo-auto-update")]
    [InlineData("ntp: {enable: true, write-to-system: true}", "write-to-system")]
    [InlineData("tcp-concurrent: true\nntp: {write-to-system: TRUE}\ngeo-auto-update: TRUE", "write-to-system")]
    public void TransparentProxyCandidate_SwitchesOffPrivilegedPreferencesInsteadOfRefusingTheProfile(string preference, string key)
    {
        string source = "rules: ['MATCH,DIRECT']\n" + preference + "\n";

        string ordinary = Candidate(source, transparentProxy: false);
        string tun = Candidate(source, transparentProxy: true);

        Assert.Contains(key, ordinary, StringComparison.Ordinal);
        YamlMappingNode root = (YamlMappingNode)LoadRoot(tun);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in Flatten(root))
        {
            if (pair.Key is YamlScalarNode { Value: "geox-url" })
            {
                Assert.Fail("The download address must not reach the service.");
            }

            if (pair.Key is YamlScalarNode { Value: "geo-auto-update" or "write-to-system" })
            {
                Assert.Equal("false", ((YamlScalarNode)pair.Value).Value, ignoreCase: true);
            }
        }
    }

    /// <summary>The same switches are found wherever they sit, as the service finds them.</summary>
    [Fact]
    public void TransparentProxyCandidate_FindsPrivilegedPreferencesInNestedStructures()
    {
        const string source = "rules: ['MATCH,DIRECT']\nproxy-providers:\n  a: {type: file, path: a.yaml, override: {geo-auto-update: true}}\n";

        string tun = Candidate(source, transparentProxy: true);

        Assert.DoesNotContain("geo-auto-update: true", tun, StringComparison.OrdinalIgnoreCase);
    }

    private static YamlNode LoadRoot(string text)
    {
        YamlStream stream = [];
        stream.Load(new StringReader(text));
        return stream.Documents[0].RootNode;
    }

    private static IEnumerable<KeyValuePair<YamlNode, YamlNode>> Flatten(YamlNode node)
    {
        Stack<YamlNode> pending = new([node]);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case YamlMappingNode mapping:
                    foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                    {
                        yield return pair;
                        pending.Push(pair.Value);
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (YamlNode child in sequence.Children)
                    {
                        pending.Push(child);
                    }

                    break;
            }
        }
    }

    [Fact]
    public void DnsListener_RemainsUsableForSystemProxyAndGetsAnActionableTunPreflight()
    {
        const string source = "proxies: []\nrules: ['MATCH,DIRECT']\ndns: {enable: true, listen: '127.0.0.1:1053'}\n";
        string ordinary = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            source, 7890, ClashSharpMode.RuleTakeover, false, new string('a', 64));
        Assert.Contains("127.0.0.1:1053", ordinary, StringComparison.Ordinal);
        StableRuntimeDiagnosticException error = Assert.Throws<StableRuntimeDiagnosticException>(() =>
            MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
                source, 7890, ClashSharpMode.RuleTakeover, true, new string('a', 64)));
        Assert.Equal(RuntimeFailureDiagnostics.TunDnsListenerUnsupported, error.DiagnosticCode);
        Assert.Equal("RuntimeFailure.TunDnsListener", RuntimeFailureDiagnostics.Describe(error.DiagnosticCode).MessageResourceKey);
    }
    [Theory]
    [InlineData("proxies: []\n")]
    [InlineData("rules: ['MATCH,DIRECT']\n")]
    [InlineData("'proxies': []\n'proxy-groups': []\n'rules': ['MATCH,DIRECT']\n")]
    [InlineData("{proxies: [], proxy-groups: [], rules: ['MATCH,DIRECT']}\n")]
    [InlineData("    proxies: []\n    proxy-groups: []\n    rules: ['MATCH,DIRECT']\n")]
    public void NativeCompatibleProfilesAreNotRejectedByTextualSectionChecks(string input)
    {
        string normalized = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(input);
        MihomoProfileShapeValidator.Validate(normalized);
    }

    [Fact]
    public void NormalizationPreservesRootIndentationAndLiteralTrailingLineBreaks()
    {
        const string Input = "    rules: ['MATCH,DIRECT']\r\n    metadata: |+\r\n      line\r\n\r\n";
        string normalized = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(Input);
        YamlStream yaml = new();
        yaml.Load(new StringReader(normalized));
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        Assert.Equal("line\n\n", Assert.IsType<YamlScalarNode>(root.Children[new YamlScalarNode("metadata")]).Value);
    }

    [Fact]
    public async Task MissingBundledCoreDoesNotSilentlySkipValidation()
    {
        string root = Path.Combine(Path.GetTempPath(), "clashsharp-missing-core-" + Guid.NewGuid().ToString("N"));
        CoreConfigurationValidator validator = new(Path.Combine(root, "missing.exe"), Path.Combine(root, "assets"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => validator.ValidateAsync(root, Path.Combine(root, "config.yaml"), CancellationToken.None));
    }
}
