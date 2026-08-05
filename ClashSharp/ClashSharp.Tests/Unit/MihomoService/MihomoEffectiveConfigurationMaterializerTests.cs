using System.Security.Cryptography;
using System.Text;
using ClashSharp.MihomoService;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Tests.Unit.MihomoService;

public sealed class MihomoEffectiveConfigurationMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_ReplacesOnlyRootControllerAuthority_WithoutChangingSource()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = Path.Combine(temporaryDirectory.Path, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        const string sourceText = """
            mixed-port: 7890
            external-controller: 127.0.0.1:9090
            external-controller-tls: 127.0.0.1:9443
            external-controller-unix: /tmp/mihomo.sock
            external-controller-pipe: '\\.\pipe\untrusted'
            external-controller-cors:
              allow-origins: ['*']
            secret: 'app-owned-secret'
            proxy-providers:
              nested:
                type: inline
                secret: preserve-this-provider-value
                external-controller: preserve-this-nested-value
            """;
        string sourcePath = Path.Combine(temporaryDirectory.Path, "generation.yaml");
        await File.WriteAllTextAsync(sourcePath, sourceText, new UTF8Encoding(false));
        byte[] originalBytes = await File.ReadAllBytesAsync(sourcePath);
        string sourceHash = ComputeHash(originalBytes);
        MihomoStagedGeneration source = new(7, sourceHash, sourcePath);
        MihomoEffectiveConfigurationMaterializer materializer = new(protectDirectory: false);

        MihomoEffectiveGeneration effective = await materializer.MaterializeAsync(
            source,
            runtimeDirectory,
            CancellationToken.None);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.Same(source, effective.Source);
        Assert.Matches(
            @"^\\\\\.\\pipe\\ClashSharp\.Mihomo\.Controller\.[0-9a-f]{64}$",
            effective.Authority.PipeName);
        Assert.Matches("^[0-9a-f]{64}$", effective.Authority.Secret);
        Assert.Equal(ComputeHash(await File.ReadAllBytesAsync(effective.ConfigurationPath)), effective.EffectiveHash);
        Assert.True(File.GetAttributes(effective.ConfigurationPath).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(effective.ConfigurationPath)!,
            "*.tmp"));

        YamlMappingNode root = LoadRoot(await File.ReadAllTextAsync(effective.ConfigurationPath));
        Assert.False(HasRootKey(root, "external-controller"));
        Assert.False(HasRootKey(root, "external-controller-tls"));
        Assert.False(HasRootKey(root, "external-controller-unix"));
        Assert.False(HasRootKey(root, "external-controller-cors"));
        Assert.Equal(effective.Authority.PipeName, GetScalar(root, "external-controller-pipe"));
        Assert.Equal(effective.Authority.Secret, GetScalar(root, "secret"));
        Assert.Equal("0", GetScalar(root, "mixed-port"));
        YamlMappingNode providers = Assert.IsType<YamlMappingNode>(GetValue(root, "proxy-providers"));
        YamlMappingNode nested = Assert.IsType<YamlMappingNode>(GetValue(providers, "nested"));
        Assert.Equal("preserve-this-provider-value", GetScalar(nested, "secret"));
        Assert.Equal("preserve-this-nested-value", GetScalar(nested, "external-controller"));

        materializer.DeleteAfterJobEmpty(effective);
        Assert.False(File.Exists(effective.ConfigurationPath));
    }

    [Fact]
    public async Task MaterializeAsync_TwoExplicitCalls_CreateIndependentAuthoritiesAndFiles()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = Path.Combine(temporaryDirectory.Path, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        const string sourceText = "mixed-port: 7890\n";
        string sourcePath = Path.Combine(temporaryDirectory.Path, "generation.yaml");
        await File.WriteAllTextAsync(sourcePath, sourceText, new UTF8Encoding(false));
        MihomoStagedGeneration source = new(
            1,
            ComputeHash(await File.ReadAllBytesAsync(sourcePath)),
            sourcePath);
        MihomoEffectiveConfigurationMaterializer materializer = new(protectDirectory: false);

        MihomoEffectiveGeneration first = await materializer.MaterializeAsync(
            source,
            runtimeDirectory,
            CancellationToken.None);
        MihomoEffectiveGeneration second = await materializer.MaterializeAsync(
            source,
            runtimeDirectory,
            CancellationToken.None);

        Assert.NotEqual(first.Authority.PipeName, second.Authority.PipeName);
        Assert.NotEqual(first.Authority.Secret, second.Authority.Secret);
        Assert.NotEqual(first.EffectiveHash, second.EffectiveHash);
        Assert.NotEqual(first.ConfigurationPath, second.ConfigurationPath);
        materializer.DeleteAfterJobEmpty(first);
        materializer.DeleteAfterJobEmpty(second);
    }

    [Fact]
    public async Task MaterializeAsync_StagesLocalProviderAndRewritesNativeHttpCachePaths()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = Path.Combine(temporaryDirectory.Path, "runtime");
        string sourceAssetRoot = Path.Combine(temporaryDirectory.Path, "app-data");
        string generationDirectory = Path.Combine(temporaryDirectory.Path, "service-generations");
        string localRulesDirectory = Path.Combine(sourceAssetRoot, "rules");
        Directory.CreateDirectory(runtimeDirectory);
        Directory.CreateDirectory(generationDirectory);
        Directory.CreateDirectory(localRulesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(localRulesDirectory, "local.yaml"),
            "payload:\n  - DOMAIN,example.com\n");
        const string sourceText = """
            mixed-port: 7890
            proxy-providers:
              remote:
                type: http
                url: https://example.invalid/proxies.yaml
                path: user/chosen/path.yaml
            rule-providers:
              local:
                type: file
                behavior: domain
                path: rules/local.yaml
            """;
        string sourcePath = Path.Combine(generationDirectory, "generation.yaml");
        await File.WriteAllTextAsync(sourcePath, sourceText, new UTF8Encoding(false));
        MihomoStagedGeneration source = new(
            2,
            ComputeHash(await File.ReadAllBytesAsync(sourcePath)),
            sourcePath);
        MihomoEffectiveConfigurationMaterializer materializer = new(
            geoDataDirectory: null,
            sourceAssetRoot,
            protectDirectory: false);

        MihomoEffectiveGeneration effective = await materializer.MaterializeAsync(
            source,
            runtimeDirectory,
            CancellationToken.None);

        YamlMappingNode root = LoadRoot(await File.ReadAllTextAsync(effective.ConfigurationPath));
        YamlMappingNode proxies = Assert.IsType<YamlMappingNode>(GetValue(root, "proxy-providers"));
        YamlMappingNode remote = Assert.IsType<YamlMappingNode>(GetValue(proxies, "remote"));
        string httpPath = GetScalar(remote, "path")!;
        Assert.StartsWith("providers/http/proxy/", httpPath, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(runtimeDirectory, httpPath)));

        YamlMappingNode rules = Assert.IsType<YamlMappingNode>(GetValue(root, "rule-providers"));
        YamlMappingNode local = Assert.IsType<YamlMappingNode>(GetValue(rules, "local"));
        string filePath = GetScalar(local, "path")!;
        Assert.StartsWith("providers/file/rule/", filePath, StringComparison.Ordinal);
        Assert.Contains(
            "DOMAIN,example.com",
            await File.ReadAllTextAsync(Path.Combine(runtimeDirectory, filePath)),
            StringComparison.Ordinal);
        Assert.Equal("false", GetScalar(root, "geo-auto-update"));
        materializer.DeleteAfterJobEmpty(effective);
    }

    [Fact]
    public async Task MaterializeAsync_GeodataConsumersRequireManifestAndStageExactAssets()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = Path.Combine(temporaryDirectory.Path, "runtime");
        string geoDirectory = Path.Combine(temporaryDirectory.Path, "GeoData");
        Directory.CreateDirectory(runtimeDirectory);
        Directory.CreateDirectory(geoDirectory);
        Dictionary<string, byte[]> assets = new(StringComparer.Ordinal)
        {
            ["Country.mmdb"] = "country-test"u8.ToArray(),
            ["GeoSite.dat"] = "site-test"u8.ToArray(),
            ["ASN.mmdb"] = "asn-test"u8.ToArray(),
        };
        foreach ((string name, byte[] bytes) in assets)
        {
            await File.WriteAllBytesAsync(Path.Combine(geoDirectory, name), bytes);
        }

        string entries = string.Join(
            ",",
            assets.Select(pair =>
                $$"""{"name":"{{pair.Key}}","length":{{pair.Value.Length}},"sha256":"{{ComputeHash(pair.Value)}}"}"""));
        await File.WriteAllTextAsync(
            Path.Combine(geoDirectory, "manifest.json"),
            $$"""{"schemaVersion":1,"files":[{{entries}}]}""");
        const string sourceText = """
            mixed-port: 7890
            rules:
              - GEOSITE,cn,DIRECT
              - GEOIP,CN,DIRECT
              - IP-ASN,13335,DIRECT
            """;
        string sourcePath = Path.Combine(temporaryDirectory.Path, "generation.yaml");
        await File.WriteAllTextAsync(sourcePath, sourceText, new UTF8Encoding(false));
        MihomoStagedGeneration source = new(
            3,
            ComputeHash(await File.ReadAllBytesAsync(sourcePath)),
            sourcePath);
        MihomoEffectiveConfigurationMaterializer materializer = new(
            geoDirectory,
            protectDirectory: false);

        MihomoEffectiveGeneration effective = await materializer.MaterializeAsync(
            source,
            runtimeDirectory,
            CancellationToken.None);

        foreach ((string name, byte[] bytes) in assets)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(runtimeDirectory, name)));
        }

        materializer.DeleteAfterJobEmpty(effective);
    }

    [Fact]
    public async Task MaterializeAsync_MissingInstallerGeodataFailsWithRepairDiagnostic()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = Path.Combine(temporaryDirectory.Path, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        string sourcePath = Path.Combine(temporaryDirectory.Path, "generation.yaml");
        await File.WriteAllTextAsync(
            sourcePath,
            "mixed-port: 7890\nrules:\n  - GEOIP,CN,DIRECT\n",
            new UTF8Encoding(false));
        MihomoStagedGeneration source = new(
            4,
            ComputeHash(await File.ReadAllBytesAsync(sourcePath)),
            sourcePath);

        MihomoRuntimeAssetException exception = await Assert.ThrowsAsync<
            MihomoRuntimeAssetException>(() =>
            new MihomoEffectiveConfigurationMaterializer(protectDirectory: false)
                .MaterializeAsync(source, runtimeDirectory, CancellationToken.None));

        Assert.Equal("geo.assets_missing", exception.ErrorCode);
        Assert.Contains("installer repair", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaterializeAsync_MissingLocalProviderReturnsStablePathDiagnostic()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = Path.Combine(temporaryDirectory.Path, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        string sourcePath = Path.Combine(temporaryDirectory.Path, "generation.yaml");
        await File.WriteAllTextAsync(
            sourcePath,
            "mixed-port: 7890\nrule-providers:\n  local:\n    type: file\n    path: rules/missing.yaml\n",
            new UTF8Encoding(false));
        MihomoStagedGeneration source = new(
            5,
            ComputeHash(await File.ReadAllBytesAsync(sourcePath)),
            sourcePath);

        MihomoRuntimeAssetException exception = await Assert.ThrowsAsync<
            MihomoRuntimeAssetException>(() =>
            new MihomoEffectiveConfigurationMaterializer(protectDirectory: false)
                .MaterializeAsync(source, runtimeDirectory, CancellationToken.None));

        Assert.Equal("provider.path_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task MaterializeAsync_SourceHashMismatch_DoesNotCreateEffectiveDirectory()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = Path.Combine(temporaryDirectory.Path, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        string sourcePath = Path.Combine(temporaryDirectory.Path, "generation.yaml");
        await File.WriteAllTextAsync(sourcePath, "mixed-port: 7890\n", new UTF8Encoding(false));
        MihomoStagedGeneration source = new(1, new string('0', 64), sourcePath);
        MihomoEffectiveConfigurationMaterializer materializer = new(protectDirectory: false);

        await Assert.ThrowsAsync<MihomoConfigurationHashMismatchException>(() =>
            materializer.MaterializeAsync(source, runtimeDirectory, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(runtimeDirectory, "effective")));
    }

    [Fact]
    public void DeleteAfterJobEmpty_RejectsConfigurationNotCreatedByThisInstance()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string unrelatedPath = Path.Combine(temporaryDirectory.Path, "unrelated.yaml");
        File.WriteAllText(unrelatedPath, "keep: true\n");
        MihomoEffectiveGeneration fabricated = new(
            new MihomoStagedGeneration(1, new string('0', 64), unrelatedPath),
            unrelatedPath,
            new string('0', 64),
            new MihomoControllerAuthority("redacted-pipe", "redacted-secret"));

        Assert.Throws<InvalidOperationException>(() =>
            new MihomoEffectiveConfigurationMaterializer(protectDirectory: false)
                .DeleteAfterJobEmpty(fabricated));
        Assert.True(File.Exists(unrelatedPath));
    }

    [Fact]
    public void Authority_ToString_DoesNotExposeCapabilities()
    {
        MihomoControllerAuthority authority = new("sensitive-pipe", "sensitive-secret");

        string display = authority.ToString();

        Assert.DoesNotContain("sensitive-pipe", display, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-secret", display, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupStaleAfterConfirmedNoOwnedJob_DeletesOnlyOwnedArtifactNames()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = Path.Combine(temporaryDirectory.Path, "runtime");
        string effectiveDirectory = Path.Combine(runtimeDirectory, "effective");
        Directory.CreateDirectory(effectiveDirectory);
        string staleEffective = Path.Combine(
            effectiveDirectory,
            $"effective-{1:D20}-{new string('a', 64)}-{new string('b', 64)}.yaml");
        string staleTemporary = Path.Combine(
            effectiveDirectory,
            $".{Guid.NewGuid():N}.tmp");
        string unrelated = Path.Combine(effectiveDirectory, "keep.txt");
        string userNote = Path.Combine(effectiveDirectory, "effective-user-note.yaml");
        File.WriteAllText(staleEffective, "secret: stale\n");
        File.WriteAllText(staleTemporary, "temporary\n");
        File.WriteAllText(unrelated, "keep\n");
        File.WriteAllText(userNote, "keep this too\n");
        File.SetAttributes(staleEffective, FileAttributes.ReadOnly);

        new MihomoEffectiveConfigurationMaterializer(protectDirectory: false)
            .CleanupStaleAfterConfirmedNoOwnedJob(runtimeDirectory);

        Assert.False(File.Exists(staleEffective));
        Assert.False(File.Exists(staleTemporary));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(userNote));
    }

    private static YamlMappingNode LoadRoot(string text)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(text));
        return Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
    }

    private static bool HasRootKey(YamlMappingNode mapping, string key) =>
        mapping.Children.Keys.OfType<YamlScalarNode>().Any(node => node.Value == key);

    private static YamlNode GetValue(YamlMappingNode mapping, string key) =>
        mapping.Children.Single(pair => pair.Key is YamlScalarNode scalar && scalar.Value == key).Value;

    private static string? GetScalar(YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlScalarNode>(GetValue(mapping, key)).Value;

    private static string ComputeHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
