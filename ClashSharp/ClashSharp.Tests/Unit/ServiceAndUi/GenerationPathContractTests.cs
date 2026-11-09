using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.MihomoService;
using ClashSharp.Tests.Unit.MihomoService;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Contract: a configuration that belongs to one data generation can only ever read provider bytes from
/// that generation's own directory. The resolver and the asset stager together must make every
/// escape (other generation, legacy root, absolute, traversal, reparse point) unrepresentable.
/// </summary>
public sealed class GenerationPathContractTests
{
    private const string CanaryText = "CANARY-FROM-OUTSIDE-THE-GENERATION";

    private static string GenerationDirectory(string appRoot, Guid id) => Path.Combine(
        appRoot,
        "Data",
        "v1",
        "generations",
        id.ToString("N"),
        "mihomo");

    [Fact]
    public void Resolve_WithoutGenerationReturnsExactlyTheInstalledPath()
    {
        using MihomoServiceTemporaryDirectory directory = new();
        string installed = Path.Combine(directory.Path, "app", "mihomo", "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "x");

        Assert.Equal(installed, MihomoServiceSourcePaths.ResolveConfiguration(installed, null));
    }

    [Fact]
    public void Resolve_EmptyGenerationIsRejectedBeforeAnyFileSystemAccess()
    {
        // Neither the path nor any of its parents exist: the identity check must come first.
        string installed = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mihomo", "config.yaml");

        Assert.Throws<InvalidDataException>(() =>
            MihomoServiceSourcePaths.ResolveConfiguration(installed, Guid.Empty));
    }

    [Fact]
    public void Resolve_IsInjectiveAndAlwaysInsideTheGenerationsRoot_ForGeneratedIdentities()
    {
        using MihomoServiceTemporaryDirectory directory = new();
        string appRoot = Path.Combine(directory.Path, "app");
        string installed = Path.Combine(appRoot, "mihomo", "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "legacy");
        string generationsRoot = Path.Combine(appRoot, "Data", "v1", "generations") + Path.DirectorySeparatorChar;
        Random random = new(20261109);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < 60; index++)
        {
            byte[] bytes = new byte[16];
            random.NextBytes(bytes);
            Guid id = new(bytes);
            if (id == Guid.Empty)
            {
                continue;
            }

            string source = GenerationDirectory(appRoot, id);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "config.yaml"), "x");

            string resolved = MihomoServiceSourcePaths.ResolveConfiguration(installed, id);

            Assert.Equal(Path.Combine(source, "config.yaml"), resolved);
            Assert.StartsWith(generationsRoot, Path.GetFullPath(resolved), StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(installed, resolved);
            Assert.True(seen.Add(resolved), "Two identities resolved to the same configuration path.");
            Assert.Equal(
                id.ToString("N", CultureInfo.InvariantCulture),
                Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(resolved))!));
        }
    }

    [Theory]
    [InlineData("config.yml", "mihomo")]
    [InlineData("config.yaml", "other")]
    [InlineData("settings.yaml", "mihomo")]
    [InlineData("config.yaml.bak", "mihomo")]
    public void Resolve_NonCanonicalInstalledShapeCannotOwnAGeneration(string fileName, string parent)
    {
        using MihomoServiceTemporaryDirectory directory = new();
        string installed = Path.Combine(directory.Path, "app", parent, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "x");
        Guid id = Guid.NewGuid();
        string generationFile = Path.Combine(GenerationDirectory(Path.Combine(directory.Path, "app"), id), "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(generationFile)!);
        File.WriteAllText(generationFile, "x");

        Assert.Throws<InvalidDataException>(() => MihomoServiceSourcePaths.ResolveConfiguration(installed, id));
    }

    [Fact]
    public void Resolve_CaseVariantsOfTheCanonicalInstalledNamesAreAccepted()
    {
        using MihomoServiceTemporaryDirectory directory = new();
        string appRoot = Path.Combine(directory.Path, "app");
        string installed = Path.Combine(appRoot, "MIHOMO", "CONFIG.YAML");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "x");
        Guid id = Guid.NewGuid();
        string source = GenerationDirectory(appRoot, id);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "config.yaml"), "x");

        Assert.Equal(
            Path.Combine(source, "config.yaml"),
            MihomoServiceSourcePaths.ResolveConfiguration(installed, id));
    }

    [Fact]
    public void Resolve_MissingGenerationNeverFallsBackToTheLegacyConfiguration()
    {
        using MihomoServiceTemporaryDirectory directory = new();
        string installed = Path.Combine(directory.Path, "app", "mihomo", "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "legacy");

        Exception? failure = Record.Exception(() =>
            MihomoServiceSourcePaths.ResolveConfiguration(installed, Guid.NewGuid()));

        Assert.True(failure is IOException or UnauthorizedAccessException, $"Unexpected outcome: {failure}");
    }

    [Theory]
    [InlineData("Data")]
    [InlineData("v1")]
    [InlineData("generations")]
    [InlineData("generation-id")]
    [InlineData("mihomo")]
    [InlineData("config.yaml")]
    public void Resolve_ReparsePointAnywhereInTheChainIsRejected(string linkedSegment)
    {
        using MihomoServiceTemporaryDirectory directory = new();
        string appRoot = Path.Combine(directory.Path, "app");
        string installed = Path.Combine(appRoot, "mihomo", "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "legacy");
        Guid id = Guid.NewGuid();

        // Build the real tree elsewhere, then replace one segment of the expected path by a link to it.
        string realRoot = Path.Combine(directory.Path, "real");
        string realGeneration = GenerationDirectory(realRoot, id);
        Directory.CreateDirectory(realGeneration);
        File.WriteAllText(Path.Combine(realGeneration, "config.yaml"), "x");
        string expectedDirectory = GenerationDirectory(appRoot, id);
        string[] segments = [
            Path.Combine(appRoot, "Data"),
            Path.Combine(appRoot, "Data", "v1"),
            Path.Combine(appRoot, "Data", "v1", "generations"),
            Path.Combine(appRoot, "Data", "v1", "generations", id.ToString("N")),
            expectedDirectory,
            Path.Combine(expectedDirectory, "config.yaml"),
        ];
        string[] realSegments = [
            Path.Combine(realRoot, "Data"),
            Path.Combine(realRoot, "Data", "v1"),
            Path.Combine(realRoot, "Data", "v1", "generations"),
            Path.Combine(realRoot, "Data", "v1", "generations", id.ToString("N")),
            realGeneration,
            Path.Combine(realGeneration, "config.yaml"),
        ];
        int linked = linkedSegment switch
        {
            "Data" => 0,
            "v1" => 1,
            "generations" => 2,
            "generation-id" => 3,
            "mihomo" => 4,
            _ => 5,
        };
        // Materialize every segment before the link as a real directory.
        Directory.CreateDirectory(Path.GetDirectoryName(segments[linked])!);
        try
        {
            if (linked == 5)
            {
                File.CreateSymbolicLink(segments[linked], realSegments[linked]);
            }
            else
            {
                Directory.CreateSymbolicLink(segments[linked], realSegments[linked]);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return; // Symbolic links are not creatable in this environment.
        }

        Assert.Throws<InvalidDataException>(() => MihomoServiceSourcePaths.ResolveConfiguration(installed, id));
    }

    [Fact]
    public async Task EachGeneration_CopiesOnlyItsOwnProviderBytes_ForGeneratedIdentitiesAndPaths()
    {
        string[] relativePaths =
        [
            "rules/local.yaml",
            "a/b/c/d.yaml",
            "unicode规则/文件.yaml",
            "with space/in name.yaml",
            "flat.yaml",
        ];
        Random random = new(20261109);

        foreach (string relativePath in relativePaths)
        {
            using MihomoServiceTemporaryDirectory directory = new();
            MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(directory.Path, generationLayout: true);
            string appRoot = Path.Combine(directory.Path, "app");
            Guid a = NewIdentity(random);
            Guid b = NewIdentity(random);
            WriteGeneration(appRoot, a, relativePath, "payload: [from-a]", ProviderConfig(relativePath));
            WriteGeneration(appRoot, b, relativePath, "payload: [from-b]", ProviderConfig(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(options.ConfigPath)!);
            string legacyFile = Path.Combine(Path.GetDirectoryName(options.ConfigPath)!, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
            File.WriteAllText(legacyFile, "payload: [from-legacy]");
            Directory.CreateDirectory(options.RuntimeDirectory);
            MihomoEffectiveConfigurationMaterializer materializer = new(options, protectDirectory: false);

            foreach ((Guid id, string expected) in new[] { (a, "from-a"), (b, "from-b"), (a, "from-a") })
            {
                string stagedText = ProviderConfig(relativePath);
                string stagedPath = Path.Combine(options.ServiceDataDirectory, $"generation-{id:N}.yaml");
                Directory.CreateDirectory(options.ServiceDataDirectory);
                await File.WriteAllTextAsync(stagedPath, stagedText);
                MihomoEffectiveGeneration effective = await materializer.MaterializeAsync(
                    new MihomoStagedGeneration(1, MihomoServiceTestSupport.ComputeHash(stagedText), stagedPath, id),
                    options.RuntimeDirectory,
                    CancellationToken.None);

                string copied = Assert.Single(
                    Directory.GetFiles(Path.Combine(options.RuntimeDirectory, "providers", "file", "rule"), "*.yaml"));
                string text = await File.ReadAllTextAsync(copied);
                Assert.Contains(expected, text, StringComparison.Ordinal);
                Assert.DoesNotContain("from-legacy", text, StringComparison.Ordinal);
                Assert.DoesNotContain(expected == "from-a" ? "from-b" : "from-a", text, StringComparison.Ordinal);
                materializer.DeleteAfterJobEmpty(effective);
            }
        }
    }

    [Theory]
    [InlineData("../outside.yaml")]
    [InlineData("..\\outside.yaml")]
    [InlineData("rules/../../outside.yaml")]
    [InlineData("rules/../local.yaml")]
    [InlineData("./rules/local.yaml")]
    [InlineData("rules//local.yaml")]
    [InlineData("rules\\\\local.yaml")]
    [InlineData("/rules/local.yaml")]
    [InlineData("\\rules\\local.yaml")]
    [InlineData("C:rules/local.yaml")]
    [InlineData("C:\\rules\\local.yaml")]
    [InlineData("\\\\?\\C:\\rules\\local.yaml")]
    [InlineData("\\\\server\\share\\local.yaml")]
    [InlineData("rules/local.yaml:hidden-stream")]
    [InlineData("rules/local.yaml.")]
    [InlineData("rules/local.yaml ")]
    [InlineData("rules/NUL")]
    [InlineData("rules/aux.yaml")]
    public async Task HostileProviderPath_NeverReachesAnotherGenerationOrTheLegacyRoot(string hostilePath)
    {
        using MihomoServiceTemporaryDirectory directory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(directory.Path, generationLayout: true);
        string appRoot = Path.Combine(directory.Path, "app");
        Guid mine = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        WriteGeneration(appRoot, mine, "rules/local.yaml", "payload: [mine]", ProviderConfig("rules/local.yaml"));
        WriteGeneration(appRoot, other, "rules/local.yaml", $"payload: [{CanaryText}]", ProviderConfig("rules/local.yaml"));
        File.WriteAllText(Path.Combine(directory.Path, "outside.yaml"), $"payload: [{CanaryText}]");
        string legacy = Path.GetDirectoryName(options.ConfigPath)!;
        Directory.CreateDirectory(Path.Combine(legacy, "rules"));
        File.WriteAllText(Path.Combine(legacy, "rules", "local.yaml"), $"payload: [{CanaryText}]");
        Directory.CreateDirectory(options.RuntimeDirectory);
        string stagedText = ProviderConfig(hostilePath);
        string stagedPath = Path.Combine(options.ServiceDataDirectory, "generation-hostile.yaml");
        Directory.CreateDirectory(options.ServiceDataDirectory);
        await File.WriteAllTextAsync(stagedPath, stagedText);
        MihomoEffectiveConfigurationMaterializer materializer = new(options, protectDirectory: false);

        Exception? failure = await Record.ExceptionAsync(() => materializer.MaterializeAsync(
            new MihomoStagedGeneration(1, MihomoServiceTestSupport.ComputeHash(stagedText), stagedPath, mine),
            options.RuntimeDirectory,
            CancellationToken.None));

        Assert.True(
            failure is MihomoServiceConfigurationTrustException or ArgumentException or IOException,
            $"Hostile path '{hostilePath}' produced {failure?.GetType().Name ?? "success"}.");
        Assert.DoesNotContain(
            Directory.EnumerateFiles(options.RuntimeDirectory, "*", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains(CanaryText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderThroughLinkedDirectoryOrFile_IsRejectedAndLeaksNothing()
    {
        foreach (bool linkDirectory in new[] { true, false })
        {
            using MihomoServiceTemporaryDirectory directory = new();
            MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(directory.Path, generationLayout: true);
            string appRoot = Path.Combine(directory.Path, "app");
            Guid mine = Guid.NewGuid();
            string generation = GenerationDirectory(appRoot, mine);
            Directory.CreateDirectory(generation);
            File.WriteAllText(Path.Combine(generation, "config.yaml"), ProviderConfig("rules/local.yaml"));
            string outside = Path.Combine(directory.Path, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "local.yaml"), $"payload: [{CanaryText}]");
            try
            {
                if (linkDirectory)
                {
                    Directory.CreateSymbolicLink(Path.Combine(generation, "rules"), outside);
                }
                else
                {
                    Directory.CreateDirectory(Path.Combine(generation, "rules"));
                    File.CreateSymbolicLink(
                        Path.Combine(generation, "rules", "local.yaml"),
                        Path.Combine(outside, "local.yaml"));
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return;
            }

            Directory.CreateDirectory(options.RuntimeDirectory);
            string stagedText = ProviderConfig("rules/local.yaml");
            Directory.CreateDirectory(options.ServiceDataDirectory);
            string stagedPath = Path.Combine(options.ServiceDataDirectory, "generation-linked.yaml");
            await File.WriteAllTextAsync(stagedPath, stagedText);
            MihomoEffectiveConfigurationMaterializer materializer = new(options, protectDirectory: false);

            Exception? failure = await Record.ExceptionAsync(() => materializer.MaterializeAsync(
                new MihomoStagedGeneration(1, MihomoServiceTestSupport.ComputeHash(stagedText), stagedPath, mine),
                options.RuntimeDirectory,
                CancellationToken.None));

            Assert.True(failure is MihomoServiceConfigurationTrustException or IOException, $"{failure}");
            Assert.DoesNotContain(
                Directory.EnumerateFiles(options.RuntimeDirectory, "*", SearchOption.AllDirectories),
                file => File.ReadAllText(file).Contains(CanaryText, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task HttpProviderCachePath_IsAStableNamespacedDigestIndependentOfAnyUserSuppliedPath()
    {
        using MihomoServiceTemporaryDirectory directory = new();
        Directory.CreateDirectory(Path.Combine(directory.Path, "runtime"));
        string runtime = Path.Combine(directory.Path, "runtime");
        string[] names = ["same", "规则", "with space", "a.b", "UPPER", "upper", new string('x', 200)];
        StringBuilder builder = new();
        builder.AppendLine("mixed-port: 7890");
        foreach (string section in new[] { "proxy-providers", "rule-providers" })
        {
            builder.Append(section).AppendLine(":");
            for (int index = 0; index < names.Length; index++)
            {
                builder.Append("  ").Append('"').Append(names[index]).AppendLine("\":");
                builder.AppendLine("    type: http");
                builder.AppendLine("    url: https://example.invalid/p.yaml");
                builder.Append("    path: user/chosen/").Append(index).AppendLine(".yaml");
            }
        }

        string sourceText = builder.ToString();
        string sourcePath = Path.Combine(directory.Path, "generation.yaml");
        await File.WriteAllTextAsync(sourcePath, sourceText);
        MihomoEffectiveConfigurationMaterializer materializer = new(protectDirectory: false);

        MihomoEffectiveGeneration effective = await materializer.MaterializeAsync(
            new MihomoStagedGeneration(1, MihomoServiceTestSupport.ComputeHash(sourceText), sourcePath),
            runtime,
            CancellationToken.None);

        YamlMappingNode root = ParseRoot(await File.ReadAllTextAsync(effective.ConfigurationPath));
        List<string> paths = [];
        foreach (string section in new[] { "proxy-providers", "rule-providers" })
        {
            string kind = section == "proxy-providers" ? "proxy" : "rule";
            YamlMappingNode providers = (YamlMappingNode)root.Children[new YamlScalarNode(section)];
            foreach ((YamlNode nameNode, YamlNode valueNode) in providers.Children)
            {
                string name = ((YamlScalarNode)nameNode).Value!;
                string path = ((YamlScalarNode)((YamlMappingNode)valueNode).Children[new YamlScalarNode("path")]).Value!;
                string expected = $"providers/http/{kind}/"
                    + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + name))).ToLowerInvariant()
                    + ".yaml";
                Assert.Equal(expected, path);
                paths.Add(path);
            }
        }

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(paths, path => path.Contains("user/chosen", StringComparison.Ordinal));
        materializer.DeleteAfterJobEmpty(effective);
    }

    [Fact]
    public async Task EffectiveConfiguration_KeepsEveryNonControllerRootValueAndOwnsTheControllerKeys_ForGeneratedSources()
    {
        Random random = new(20261109);
        string[] ownedKeys =
        [
            "external-controller-pipe", "secret", "mixed-port", "geo-auto-update",
        ];
        string[] droppedKeys =
        [
            "external-controller", "external-controller-tls", "external-controller-unix", "external-controller-cors",
        ];

        for (int round = 0; round < 25; round++)
        {
            using MihomoServiceTemporaryDirectory directory = new();
            string runtime = Path.Combine(directory.Path, "runtime");
            Directory.CreateDirectory(runtime);
            StringBuilder builder = new();
            List<string> expectedKeys = [];
            foreach (string key in droppedKeys.Concat(["secret", "mixed-port"]).OrderBy(_ => random.Next()))
            {
                if (random.Next(2) == 0 || key == "mixed-port")
                {
                    builder.Append(key).Append(": ").AppendLine(key == "mixed-port" ? "7890" : "'hostile-" + key + "'");
                }
            }

            int extra = random.Next(1, 8);
            for (int index = 0; index < extra; index++)
            {
                string key = "k" + index.ToString(CultureInfo.InvariantCulture) + RandomText(random, 3);
                expectedKeys.Add(key);
                builder.Append(key).Append(": ").AppendLine(RandomValue(random));
            }

            string sourceText = builder.ToString();
            string sourcePath = Path.Combine(directory.Path, "generation.yaml");
            await File.WriteAllTextAsync(sourcePath, sourceText);
            MihomoEffectiveConfigurationMaterializer materializer = new(protectDirectory: false);

            MihomoEffectiveGeneration effective = await materializer.MaterializeAsync(
                new MihomoStagedGeneration(round + 1, MihomoServiceTestSupport.ComputeHash(sourceText), sourcePath),
                runtime,
                CancellationToken.None);

            YamlMappingNode source = ParseRoot(sourceText);
            YamlMappingNode effectiveRoot = ParseRoot(await File.ReadAllTextAsync(effective.ConfigurationPath));
            Dictionary<string, YamlNode> effectiveItems = effectiveRoot.Children
                .ToDictionary(pair => ((YamlScalarNode)pair.Key).Value!, pair => pair.Value, StringComparer.Ordinal);
            Assert.Equal(
                effectiveRoot.Children.Count,
                effectiveItems.Count);
            foreach (string dropped in droppedKeys)
            {
                Assert.DoesNotContain(dropped, effectiveItems.Keys);
            }

            foreach (string owned in ownedKeys)
            {
                Assert.Contains(owned, effectiveItems.Keys);
            }

            Assert.Equal(effective.Authority.PipeName, ((YamlScalarNode)effectiveItems["external-controller-pipe"]).Value);
            Assert.Equal(effective.Authority.Secret, ((YamlScalarNode)effectiveItems["secret"]).Value);
            Assert.Equal("0", ((YamlScalarNode)effectiveItems["mixed-port"]).Value);
            Assert.Equal("false", ((YamlScalarNode)effectiveItems["geo-auto-update"]).Value);
            foreach (string key in expectedKeys)
            {
                YamlNode original = source.Children[new YamlScalarNode(key)];
                Assert.Equal(Render(original), Render(effectiveItems[key]));
            }

            Assert.DoesNotContain(
                "hostile-",
                await File.ReadAllTextAsync(effective.ConfigurationPath),
                StringComparison.Ordinal);
            materializer.DeleteAfterJobEmpty(effective);
        }
    }

    private static Guid NewIdentity(Random random)
    {
        byte[] bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static string ProviderConfig(string path)
    {
        return "mixed-port: 7890\n"
            + "rule-providers:\n"
            + "  local:\n"
            + "    type: file\n"
            + "    behavior: domain\n"
            + $"    path: '{path.Replace("'", "''", StringComparison.Ordinal)}'\n";
    }

    private static void WriteGeneration(
        string appRoot,
        Guid id,
        string providerRelativePath,
        string providerContent,
        string configuration)
    {
        string directory = GenerationDirectory(appRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config.yaml"), configuration);
        string provider = Path.Combine(directory, providerRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(provider)!);
        File.WriteAllText(provider, providerContent);
    }

    private static YamlMappingNode ParseRoot(string text)
    {
        YamlStream stream = new();
        using StringReader reader = new(text);
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string RandomText(Random random, int length)
    {
        const string alphabet = "abcXYZ019-_.é中";
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
    }

    private static string RandomValue(Random random)
    {
        return random.Next(5) switch
        {
            0 => random.Next(0, 100000).ToString(CultureInfo.InvariantCulture),
            1 => "'" + RandomText(random, random.Next(1, 40)) + "'",
            2 => "[" + string.Join(", ", Enumerable.Range(0, random.Next(0, 5)).Select(_ => "'" + RandomText(random, 4) + "'")) + "]",
            3 => "{a: '" + RandomText(random, 6) + "', b: [1, 2, 3]}",
            _ => RandomText(random, random.Next(1, 20)).Replace('.', 'x').Replace('-', 'y'),
        };
    }

    private static string Render(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => "s:" + scalar.Value,
            YamlSequenceNode sequence => "[" + string.Join(",", sequence.Children.Select(Render)) + "]",
            YamlMappingNode mapping => "{" + string.Join(",", mapping.Children.Select(pair => Render(pair.Key) + "=" + Render(pair.Value))) + "}",
            _ => node.GetType().Name,
        };
    }
}
