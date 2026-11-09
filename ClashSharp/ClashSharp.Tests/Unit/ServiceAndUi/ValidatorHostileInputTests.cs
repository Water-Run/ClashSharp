using System.Reflection;
using System.Text;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;
using ClashSharp.Tests.Unit.MihomoService;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Hostile-document contract for every YAML loader of the LocalSystem service: whatever bytes a profile
/// carries, the outcome is acceptance or a MihomoServiceConfigurationTrustException, never a process
/// crash or an exception type the service pipeline does not classify, and accepted documents always
/// satisfy the policy that an independent oracle restates.
/// </summary>
public sealed class ValidatorHostileInputTests
{
    private const string Baseline = "mixed-port: 7890\n"
        + "proxies:\n  - name: a\n    type: ss\n    server: x\n    port: 1\n"
        + "rules:\n  - MATCH,DIRECT\n"
        + "proxy-providers:\n  p:\n    type: http\n    url: https://e.invalid/x\n    path: a/b.yaml\n";

    /// <summary>Gets the nesting shapes.</summary>
    public static TheoryData<string> NestingShapes => ["flow-sequence", "flow-mapping", "block-mapping", "block-sequence", "mixed"];

    /// <summary>Gets depths below, at and far beyond the limit, up to what overflows an unguarded loader.</summary>
    public static TheoryData<string, int> NestingCases
    {
        get
        {
            TheoryData<string, int> data = [];
            foreach (string shape in new[] { "flow-sequence", "flow-mapping", "block-mapping", "block-sequence", "mixed" })
            {
                foreach (int depth in new[] { 5, 20, 40, 70, 200, 5000, 100_000 })
                {
                    if (depth > 5000 && shape.StartsWith("block", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    data.Add(shape, depth);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NestingCases))]
    public void DeepNesting_IsRefusedBeforeTheRecursiveLoaderCanOverflowTheStack(string shape, int depth)
    {
        string document = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            BaselineWithout("proxies:") + "deep: " + Nest(shape, depth));

        Exception? failure = Record.Exception(() => MihomoServiceConfigurationTrustValidator.ValidatePolicyText(document));

        if (depth <= 40)
        {
            Assert.Null(failure);
        }
        else if (depth > MihomoServiceConfigurationTrustValidator.MaximumYamlNesting + 8)
        {
            Assert.IsType<MihomoServiceConfigurationTrustException>(failure);
        }
        else
        {
            Assert.True(failure is null or MihomoServiceConfigurationTrustException);
        }
    }

    [Fact]
    public void NestingLimit_IsExactAtTheDocumentedBoundary()
    {
        int limit = MihomoServiceConfigurationTrustValidator.MaximumYamlNesting;
        // The root mapping already counts as one level; the managed tun mapping stays a sibling.
        string AtDepth(int totalDepth) => MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\nx: " + new string('[', totalDepth - 1) + new string(']', totalDepth - 1) + "\n");

        Assert.True(MihomoServiceConfigurationTrustValidator.IsYamlNestingWithinLimit(AtDepth(limit)));
        Assert.False(MihomoServiceConfigurationTrustValidator.IsYamlNestingWithinLimit(AtDepth(limit + 1)));
    }

    [Fact]
    public async Task StartingADeeplyNestedProfile_IsRefusedAsUntrustedAndTheServiceStaysUsable()
    {
        await using SupervisorHarness harness = new();
        string text = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\nx: " + new string('[', 100_000) + new string(']', 100_000) + "\n");
        await File.WriteAllTextAsync(harness.Options.ConfigPath, text);

        MihomoChildOperationResult result = await harness.Supervisor.StartAsync(
            1,
            MihomoServiceTestSupport.ComputeHash(text),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("service.child.configuration_untrusted", result.ErrorCode);
        Assert.Equal(0, harness.Launcher.Attempts);
        Assert.Empty(harness.StagedGenerationFiles());
        Assert.Equal(MihomoServiceChildState.Stopped, result.Snapshot.ChildState);
        Assert.True((await harness.Supervisor.StartAsync(2, harness.WriteNewConfiguration(), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public void AppSideProfileLoader_RefusesDeepNestingAndScannerFailuresAsInvalidInput()
    {
        // The App parses an imported profile with its own loader before the service ever sees it.
        string deep = "mixed-port: 7890\nx: " + new string('[', 100_000) + new string(']', 100_000) + "\n";
        Assert.Throws<ArgumentException>(() =>
            ClashSharp.Service.MihomoYamlSemanticValidator.LoadUniqueRootMappingNode(deep, "text"));

        // A scanner state failure (unclosed flow sequence followed by block content) is invalid YAML.
        string broken = "mixed-port: 7890\ntun:\n  enable: [true\n  stack: system\n  auto-route: true\n  dns-hijack:\n    - any:53\n";
        Assert.Throws<ArgumentException>(() =>
            ClashSharp.Service.MihomoYamlSemanticValidator.LoadUniqueRootMappingNode(broken, "text"));
    }

    [Fact]
    public async Task EveryLoaderRefusesDeepNesting_WithoutCrashing()
    {
        string deep = "x: " + new string('[', 100_000) + new string(']', 100_000) + "\n";
        string document = MihomoServiceTestSupport.BuildManagedServiceConfiguration("mixed-port: 7890\n" + deep);
        using MihomoServiceTemporaryDirectory directory = new();
        string runtime = Path.Combine(directory.Path, "runtime");
        Directory.CreateDirectory(runtime);
        string path = Path.Combine(directory.Path, "g.yaml");
        await File.WriteAllTextAsync(path, document);

        await Assert.ThrowsAsync<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateAsync(path, runtime, CancellationToken.None));
        await Assert.ThrowsAsync<MihomoServiceConfigurationTrustException>(() =>
            new MihomoEffectiveConfigurationMaterializer(protectDirectory: false).MaterializeAsync(
                new MihomoStagedGeneration(1, MihomoServiceTestSupport.ComputeHash(document), path),
                runtime,
                CancellationToken.None));
        await Assert.ThrowsAsync<MihomoServiceConfigurationTrustException>(() =>
            MihomoRuntimeConfigurationPlan.ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public void MutatedDocuments_AreAcceptedOnlyWhenAnIndependentOraclePolicyHolds_AndNeverFailWithAnUnclassifiedException()
    {
        Random random = new(20261109);
        string baseline = MihomoServiceTestSupport.BuildManagedServiceConfiguration(Baseline);
        string special = "[]{}:-?&*!|>'\"%@#,\t\r\0\n \\~`<=é中\ud800\u0085\u2028";
        int accepted = 0;

        for (int round = 0; round < 4000; round++)
        {
            StringBuilder text = new(baseline);
            int mutations = random.Next(1, 4);
            for (int mutation = 0; mutation < mutations; mutation++)
            {
                int position = random.Next(text.Length);
                switch (random.Next(4))
                {
                    case 0:
                        text.Remove(position, 1);
                        break;
                    case 1:
                        text.Insert(position, special[random.Next(special.Length)]);
                        break;
                    case 2:
                        text[position] = special[random.Next(special.Length)];
                        break;
                    default:
                        // Duplicate a random line, the commonest way to create hostile duplicates.
                        string[] lines = text.ToString().Split('\n');
                        text.Append('\n').Append(lines[random.Next(lines.Length)]);
                        break;
                }
            }

            string mutated = text.ToString();
            Exception? failure = Record.Exception(() => MihomoServiceConfigurationTrustValidator.ValidatePolicyText(mutated));

            Assert.True(
                failure is null or MihomoServiceConfigurationTrustException,
                $"Round {round}: {failure?.GetType().Name} escaped the validator contract.\n{failure}\n{mutated}");
            if (failure is null)
            {
                accepted++;
                AssertPolicyHolds(mutated, round);
            }
        }

        Assert.True(accepted > 50, "The mutation corpus no longer exercises acceptance.");
    }

    [Fact]
    public void RandomByteSoup_NeverEscapesAsAnUnclassifiedException()
    {
        Random random = new(20261109);
        char[] alphabet = "ab:-[]{}#&*!?|>'\"\n \t0123456789é".ToCharArray();
        for (int round = 0; round < 3000; round++)
        {
            int length = random.Next(0, 400);
            string soup = new(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

            Exception? failure = Record.Exception(() => MihomoServiceConfigurationTrustValidator.ValidatePolicyText(soup));

            Assert.True(
                failure is null or MihomoServiceConfigurationTrustException,
                $"Round {round}: {failure?.GetType().Name}\n{soup}");
        }
    }

    /// <summary>Gets the encodings a file can legitimately arrive in.</summary>
    public static TheoryData<string> Encodings => ["utf8", "utf8-bom", "utf16le-bom", "utf16be-bom", "utf32le-bom"];

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task PolicyDecision_DoesNotDependOnTheFileEncoding(string encoding)
    {
        using MihomoServiceTemporaryDirectory directory = new();
        string runtime = Path.Combine(directory.Path, "runtime");
        Directory.CreateDirectory(runtime);
        string good = MihomoServiceTestSupport.BuildManagedServiceConfiguration(Baseline);
        string bad = MihomoServiceTestSupport.BuildManagedServiceConfiguration(Baseline + "external-ui: /x\n");

        await WriteAsync(Path.Combine(directory.Path, "good.yaml"), good, encoding);
        await WriteAsync(Path.Combine(directory.Path, "bad.yaml"), bad, encoding);

        await MihomoServiceConfigurationTrustValidator.ValidateAsync(
            Path.Combine(directory.Path, "good.yaml"), runtime, CancellationToken.None);
        await Assert.ThrowsAsync<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateAsync(
                Path.Combine(directory.Path, "bad.yaml"), runtime, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidUtf8OrMissingFile_IsAnUntrustedConfiguration()
    {
        using MihomoServiceTemporaryDirectory directory = new();
        string runtime = Path.Combine(directory.Path, "runtime");
        Directory.CreateDirectory(runtime);
        string path = Path.Combine(directory.Path, "g.yaml");
        string good = MihomoServiceTestSupport.BuildManagedServiceConfiguration(Baseline);
        byte[] valid = new UTF8Encoding(false).GetBytes(good);
        byte[][] corrupt =
        [
            [.. valid, 0xFF, 0xFE, 0xFD],
            [.. valid[..40], 0xC0, 0xAF, .. valid[40..]],
            [.. valid[..40], 0xED, 0xA0, 0x80, .. valid[40..]],
            [0xFF, 0xFF, 0xFF],
        ];

        foreach (byte[] bytes in corrupt)
        {
            await File.WriteAllBytesAsync(path, bytes);
            await Assert.ThrowsAsync<MihomoServiceConfigurationTrustException>(() =>
                MihomoServiceConfigurationTrustValidator.ValidateAsync(path, runtime, CancellationToken.None));
        }

        File.Delete(path);
        await Assert.ThrowsAsync<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateAsync(path, runtime, CancellationToken.None));
        await Assert.ThrowsAsync<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateAsync(path, Path.Combine(directory.Path, "missing-runtime"), CancellationToken.None));
    }

    [Fact]
    public async Task FileSizeLimit_IsEightMebibytes_ExactlyAtTheBoundary()
    {
        const int limit = 8 * 1024 * 1024;
        using MihomoServiceTemporaryDirectory directory = new();
        string runtime = Path.Combine(directory.Path, "runtime");
        Directory.CreateDirectory(runtime);
        string good = MihomoServiceTestSupport.BuildManagedServiceConfiguration(Baseline);

        string atLimit = Path.Combine(directory.Path, "at.yaml");
        string overLimit = Path.Combine(directory.Path, "over.yaml");
        await File.WriteAllTextAsync(atLimit, Pad(good, limit));
        await File.WriteAllTextAsync(overLimit, Pad(good, limit + 1));
        Assert.Equal(limit, new FileInfo(atLimit).Length);

        await MihomoServiceConfigurationTrustValidator.ValidateAsync(atLimit, runtime, CancellationToken.None);
        await Assert.ThrowsAsync<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateAsync(overLimit, runtime, CancellationToken.None));
    }

    [Fact]
    public void NodeCountLimit_RejectsWideDocumentsAndAcceptsOrdinaryOnes()
    {
        string Wide(int count) => MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\nlist:\n" + string.Concat(Enumerable.Repeat("  - a\n", count)));

        MihomoServiceConfigurationTrustValidator.ValidatePolicyText(Wide(1_000));
        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidatePolicyText(Wide(250_000)));
    }

    [Fact]
    public void AliasGraphs_AreValidatedWithoutExpansion_EvenWhenTheyWouldExpandAstronomically()
    {
        StringBuilder builder = new();
        builder.AppendLine("mixed-port: 7890");
        builder.AppendLine("l0: &l0 [aaaaaaaaaaaaaaaa, bbbbbbbbbbbbbbbb]");
        for (int level = 1; level <= 12; level++)
        {
            builder.Append('l').Append(level).Append(": &l").Append(level).Append(" [");
            builder.Append(string.Join(", ", Enumerable.Repeat("*l" + (level - 1), 10)));
            builder.AppendLine("]");
        }

        // 10^12 expanded scalars from a 1 KB document: must be neither slow nor memory hungry.
        string document = MihomoServiceTestSupport.BuildManagedServiceConfiguration(builder.ToString());
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        MihomoServiceConfigurationTrustValidator.ValidatePolicyText(document);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Alias graph validation took {watch.Elapsed}.");
    }

    [Fact]
    public async Task AliasGraphs_AreMaterializedWithoutExpansion()
    {
        StringBuilder builder = new();
        builder.AppendLine("mixed-port: 7890");
        builder.AppendLine("l0: &l0 [aaaaaaaaaaaaaaaa, bbbbbbbbbbbbbbbb]");
        for (int level = 1; level <= 12; level++)
        {
            builder.Append('l').Append(level).Append(": &l").Append(level).Append(" [");
            builder.Append(string.Join(", ", Enumerable.Repeat("*l" + (level - 1), 10)));
            builder.AppendLine("]");
        }

        string source = MihomoServiceTestSupport.BuildManagedServiceConfiguration(builder.ToString());
        using MihomoServiceTemporaryDirectory directory = new();
        string runtime = Path.Combine(directory.Path, "runtime");
        Directory.CreateDirectory(runtime);
        string path = Path.Combine(directory.Path, "g.yaml");
        await File.WriteAllTextAsync(path, source);

        MihomoEffectiveGeneration effective = await new MihomoEffectiveConfigurationMaterializer(protectDirectory: false)
            .MaterializeAsync(
                new MihomoStagedGeneration(1, MihomoServiceTestSupport.ComputeHash(source), path),
                runtime,
                CancellationToken.None);

        Assert.True(new FileInfo(effective.ConfigurationPath).Length < 8 * source.Length + 4096);
    }

    [Fact]
    public void SelfReferentialAliases_AreValidatedWithoutLooping()
    {
        string document = MihomoServiceTestSupport.BuildManagedServiceConfiguration("mixed-port: 7890\nx: &a [*a]\ny: &b {k: *b}\n");

        Exception? failure = Record.Exception(() => MihomoServiceConfigurationTrustValidator.ValidatePolicyText(document));

        Assert.True(failure is null or MihomoServiceConfigurationTrustException);
    }

    private static string BaselineWithout(string prefix)
    {
        // Keeps the document small and valid while removing a section that the nesting probe replaces.
        List<string> lines = [];
        bool skipping = false;
        foreach (string line in Baseline.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                skipping = true;
                continue;
            }

            if (skipping && line.StartsWith(' '))
            {
                continue;
            }

            skipping = false;
            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    private static string Nest(string shape, int depth)
    {
        switch (shape)
        {
            case "flow-sequence":
                return new string('[', depth) + new string(']', depth) + "\n";
            case "flow-mapping":
                return string.Concat(Enumerable.Repeat("{a: ", depth)) + "1" + new string('}', depth) + "\n";
            case "block-mapping":
                {
                    StringBuilder builder = new("\n");
                    for (int level = 0; level < depth; level++)
                    {
                        builder.Append(' ', level + 2).Append('k').Append(level).Append(":\n");
                    }

                    builder.Append(' ', depth + 2).Append("leaf: 1\n");
                    return builder.ToString();
                }

            case "block-sequence":
                {
                    StringBuilder builder = new("\n  ");
                    for (int level = 0; level < depth; level++)
                    {
                        builder.Append("- ");
                    }

                    builder.Append("leaf\n");
                    return builder.ToString();
                }

            default:
                return string.Concat(Enumerable.Repeat("[{a: ", depth / 2)) + "1" + string.Concat(Enumerable.Repeat("}]", depth / 2)) + "\n";
        }
    }

    private static string Pad(string content, int totalBytes)
    {
        StringBuilder builder = new(content);
        int missing = totalBytes - Encoding.UTF8.GetByteCount(content);
        Assert.True(missing >= 2);
        // One comment of exactly the missing size ("#" plus filler and the final newline).
        builder.Append('#').Append('x', missing - 2).Append('\n');
        return builder.ToString();
    }

    private static async Task WriteAsync(string path, string text, string encoding)
    {
        byte[] bytes = encoding switch
        {
            "utf8" => new UTF8Encoding(false).GetBytes(text),
            "utf8-bom" => [.. new UTF8Encoding(true).GetPreamble(), .. new UTF8Encoding(false).GetBytes(text)],
            "utf16le-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            "utf16be-bom" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
            _ => [.. new UTF32Encoding(false, true).GetPreamble(), .. new UTF32Encoding(false, true).GetBytes(text)],
        };
        await File.WriteAllBytesAsync(path, bytes);
    }

    /// <summary>Restates the service policy independently of the validator under test.</summary>
    private static void AssertPolicyHolds(string text, int round)
    {
        string[] rootForbidden = [.. ReadSet("ForbiddenExternalUiKeys"), .. ReadSet("ForbiddenServiceRootKeys"), .. ReadSet("ForbiddenControllerKeys")];
        string[] anyDepthForbidden = [.. ReadSet("ForbiddenLocalCredentialKeys"), "state-dir", "file-descriptor"];
        YamlStream stream = new();
        using StringReader reader = new(text);
        stream.Load(reader);
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
        Dictionary<string, YamlNode> items = root.Children.ToDictionary(
            pair => Assert.IsType<YamlScalarNode>(pair.Key).Value!,
            pair => pair.Value,
            StringComparer.Ordinal);
        string label = $"round {round}\n{text}";

        Assert.True(rootForbidden.All(key => !items.ContainsKey(key)), $"forbidden root key accepted, {label}");
        Assert.Equal("127.0.0.1:9090", Assert.IsType<YamlScalarNode>(items["external-controller"]).Value);
        Assert.True(MihomoServiceIpcProtocol.IsCanonicalSha256(Assert.IsType<YamlScalarNode>(items["secret"]).Value), label);
        Assert.Equal("false", Assert.IsType<YamlScalarNode>(items["allow-lan"]).Value);
        Assert.Equal("127.0.0.1", Assert.IsType<YamlScalarNode>(items["bind-address"]).Value);
        string? mode = Assert.IsType<YamlScalarNode>(items["mode"]).Value;
        Assert.True(mode is "rule" or "global", label);
        Assert.True(int.TryParse(Assert.IsType<YamlScalarNode>(items["mixed-port"]).Value, out int port) && port is >= 1 and <= 65535, label);
        YamlMappingNode tun = Assert.IsType<YamlMappingNode>(items["tun"]);
        Assert.Equal(6, tun.Children.Count);

        HashSet<YamlNode> visited = new(ReferenceEqualityComparer.Instance);
        Stack<YamlNode> pending = new([root]);
        while (pending.TryPop(out YamlNode? node))
        {
            if (!visited.Add(node))
            {
                continue;
            }

            switch (node)
            {
                case YamlMappingNode mapping:
                    foreach ((YamlNode key, YamlNode value) in mapping.Children)
                    {
                        if (key is YamlScalarNode { Value: string name })
                        {
                            Assert.DoesNotContain(name, anyDepthForbidden);
                            Assert.NotEqual("<<", name);
                        }

                        pending.Push(key);
                        pending.Push(value);
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

    private static string[] ReadSet(string fieldName)
    {
        FieldInfo field = typeof(MihomoServiceConfigurationTrustValidator)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        return [.. (HashSet<string>)field.GetValue(null)!];
    }
}
