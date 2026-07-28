using System.Text.RegularExpressions;

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards mutation and lifetime dependency rules that cannot be proven by behavior tests alone.</summary>
public sealed class MutationLifetimeArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>Rejects blocking waits and obsolete synchronous sampling lifecycle entry points.</summary>
    [Fact]
    public void ProductionSources_DoNotUseSyncOverAsyncOrLegacySamplingLifecycle()
    {
        IReadOnlyList<ProductionSource> sources = ReadProductionSources();

        Assert.DoesNotContain(sources, source => source.Text.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => Regex.IsMatch(source.Text, @"\bTask\.Wait(?:All|Any)?\s*\("));

        ProductionSource sampling = Assert.Single(
            sources,
            source => source.RelativePath.EndsWith("/Service/ConnectionSamplingService.cs", StringComparison.Ordinal));
        Assert.DoesNotContain("void RestartFromSettings(", sampling.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("void Stop(", sampling.Text, StringComparison.Ordinal);
    }

    /// <summary>Rejects detached Task.Run work while allowing explicitly awaited, returned, or tracked calls.</summary>
    [Fact]
    public void ProductionTaskRunCalls_AreOwnedByTheirCaller()
    {
        foreach (ProductionSource source in ReadProductionSources())
        {
            string[] lines = source.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            foreach (string line in lines.Where(static line => line.Contains("Task.Run(", StringComparison.Ordinal)))
            {
                string trimmed = line.Trim();
                bool isOwned = trimmed.Contains("await Task.Run(", StringComparison.Ordinal)
                    || trimmed.StartsWith("return Task.Run(", StringComparison.Ordinal)
                    || Regex.IsMatch(trimmed, @"^Task(?:<.+>)?\s+\w+\s*=\s*Task\.Run\(");
                Assert.True(isOwned, $"Unowned Task.Run in {source.RelativePath}: {trimmed}");
            }
        }
    }

    /// <summary>Rejects detached async continuations now that trigger scheduling is host-owned.</summary>
    [Fact]
    public void DetachedAsyncContinuations_AreAbsentFromProductionSources()
    {
        ProductionSource[] sources = ReadProductionSources()
            .Where(source => Regex.IsMatch(source.Text, @"_\s*=\s*\w+Async\s*\("))
            .ToArray();

        Assert.Empty(sources);
    }

    /// <summary>Restricts direct network side effects to the registered legacy compatibility implementation.</summary>
    [Fact]
    public void DirectNetworkSideEffects_AreRestrictedToRegisteredCompatibilityPaths()
    {
        HashSet<string> allowedPaths = new(StringComparer.Ordinal)
        {
            "ClashSharp/ClashSharp/AppHost/Compatibility/LegacyNetworkStateAdapter.cs",
            "ClashSharp/ClashSharp/AppHost/Compatibility/LegacyAppDataMaintenanceRuntimeAdapter.cs",
            "ClashSharp/ClashSharp/Service/NetworkTakeoverService.cs",
            "ClashSharp/ClashSharp/Service/NetworkTakeoverServiceFactory.cs",
        };
        const string directMutationPattern =
            @"\b_?windowsProxy\.(?:EnableProxy|DisableProxy)\b|"
            + @"\b_?core\.(?:Restart|Start|Stop)\b|"
            + @"\b(?:WindowsProxyService|MihomoCoreService)\.Instance\.(?:EnableProxy|DisableProxy|Restart|Start|Stop)\b";

        ProductionSource[] mutationSources = ReadProductionSources()
            .Where(source => Regex.IsMatch(source.Text, directMutationPattern))
            .ToArray();

        Assert.NotEmpty(mutationSources);
        Assert.All(mutationSources, source => Assert.Contains(source.RelativePath, allowedPaths));

        string host = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "ClashSharp",
            "AppHost",
            "ClashSharpAppHostFactory.cs"));
        Assert.Contains("AddSingleton<INetworkStateAdapter, LegacyNetworkStateAdapter>()", host, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<INetworkStateCommitter, LegacyNetworkStateCommitter>()", host, StringComparison.Ordinal);

        string maintenanceFactory = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "ClashSharp",
            "Service",
            "AppDataMaintenanceServiceFactory.cs"));
        Assert.Contains("new LegacyAppDataMaintenanceRuntimeAdapter(", maintenanceFactory, StringComparison.Ordinal);
    }

    /// <summary>Ensures process termination remains owned by the WinUI application root.</summary>
    [Fact]
    public void EnvironmentExit_IsNotUsedBelowTheApplicationRoot()
    {
        Assert.DoesNotContain(
            ReadProductionSources(),
            source => source.Text.Contains("Environment.Exit", StringComparison.Ordinal));
    }

    private static IReadOnlyList<ProductionSource> ReadProductionSources()
    {
        string[] roots =
        [
            "ClashSharp/ClashSharp",
            "ClashSharp/ClashSharp.Application",
            "ClashSharp/ClashSharp.Core",
            "ClashSharp/ClashSharp.Infrastructure",
            "ClashSharp/ClashSharp.MihomoService",
            "ClashSharp/ClashSharp.ProcessProbe",
            "ClashSharp/ClashSharp.StartupProbe",
        ];

        return roots
            .Select(root => Path.Combine(RepositoryRoot, root.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !HasGeneratedSegment(path))
            .Select(path => new ProductionSource(
                Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'),
                File.ReadAllText(path)))
            .OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasGeneratedSegment(string path)
    {
        string relativePath = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');
        return relativePath.Contains("/bin/", StringComparison.Ordinal)
            || relativePath.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ClashSharp repository root.");
    }

    private sealed record ProductionSource(string RelativePath, string Text);
}
