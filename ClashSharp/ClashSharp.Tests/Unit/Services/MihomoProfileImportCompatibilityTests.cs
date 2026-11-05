using ClashSharp.Model;
using ClashSharp.Service;
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

    [Fact]
    public void ServicePolicy_PreflightsGeoxUrlBeforeTunPromotion()
    {
        const string source = "rules: ['MATCH,DIRECT']\ngeox-url: {geoip: 'https://example.invalid/geoip.dat'}\n";
        _ = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(source, 7890, ClashSharpMode.RuleTakeover, false, new string('a', 64));
        StableRuntimeDiagnosticException failure = Assert.Throws<StableRuntimeDiagnosticException>(() =>
            MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(source, 7890, ClashSharpMode.RuleTakeover, true, new string('a', 64)));
        Assert.Equal(RuntimeFailureDiagnostics.TunPolicyUnsupported, failure.DiagnosticCode);
        Assert.Contains("geox-url", failure.Message, StringComparison.Ordinal);
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
