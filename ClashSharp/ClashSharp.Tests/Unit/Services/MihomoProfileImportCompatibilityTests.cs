using ClashSharp.Service;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Tests.Unit.Services;

public sealed class MihomoProfileImportCompatibilityTests
{
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
