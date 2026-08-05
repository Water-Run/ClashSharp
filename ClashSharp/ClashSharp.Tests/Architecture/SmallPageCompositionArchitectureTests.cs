using System.Text.RegularExpressions;

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the composition boundary for the small navigable pages.</summary>
public sealed class SmallPageCompositionArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ApplicationRoot =
        Path.Combine(RepositoryRoot, "ClashSharp", "ClashSharp");
    private static readonly string[] GovernedPageNames =
    [
        "About",
        "Connections",
        "Links",
        "Logs",
        "Profiles",
        "Proxies",
        "Rules",
        "Statistics",
    ];

    /// <summary>Verifies code-behind consumes explicit dependencies instead of locating services.</summary>
    [Fact]
    public void GovernedViews_DelegateDefaultConstructionToComposition()
    {
        foreach (string pageName in GovernedPageNames)
        {
            string relativePath = $"View/{pageName}.xaml.cs";
            string source = ReadApplicationSource(relativePath);

            Assert.DoesNotContain(".Instance", source, StringComparison.Ordinal);
            Assert.DoesNotContain("using ClashSharp.Service;", source, StringComparison.Ordinal);
            Assert.DoesNotMatch(
                new Regex(
                    @"\bnew\s+[A-Za-z_][A-Za-z0-9_]*(?:Service|Adapter)\b",
                    RegexOptions.CultureInvariant),
                source);
            Assert.Matches(
                new Regex(
                    $@"public\s+{pageName}\s*\(\s*\)\s*:\s*this\s*\(\s*{pageName}PageComposition\.Create\s*\(\s*\)\s*\)",
                    RegexOptions.CultureInvariant),
                source);
            Assert.Contains(
                $"internal {pageName}({pageName}PageComposition.Dependencies dependencies)",
                source,
                StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies each page factory remains a non-visual composition boundary.</summary>
    [Fact]
    public void GovernedPageCompositions_DoNotDependOnXamlOrLocateServicesDirectly()
    {
        foreach (string pageName in GovernedPageNames)
        {
            string relativePath = $"Presentation/Composition/{pageName}PageComposition.cs";
            string source = ReadApplicationSource(relativePath);

            Assert.Contains(
                $"internal static class {pageName}PageComposition",
                source,
                StringComparison.Ordinal);
            Assert.Contains("public static Dependencies Create()", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".Instance", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.UI.Xaml", source, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies legacy singletons remain isolated to one documented lazy bridge.</summary>
    [Fact]
    public void LegacyPageServices_AreIsolatedToTheLazyCompositionBridge()
    {
        string bridge = ReadApplicationSource(
            "Presentation/Composition/LegacyPageServiceBridge.cs");

        Assert.Equal(13, Regex.Count(bridge, @"\.Instance\b", RegexOptions.CultureInvariant));
        Assert.Contains("intentionally lazy", bridge, StringComparison.Ordinal);
    }

    /// <summary>Verifies profile fallback state crosses the composition boundary explicitly.</summary>
    [Fact]
    public void Profiles_InjectsActiveProfileFallback()
    {
        string composition = ReadApplicationSource(
            "Presentation/Composition/ProfilesPageComposition.cs");
        string viewModel = ReadApplicationSource("ViewModel/ProfilesViewModel.cs");

        Assert.Contains(
            "() => LegacyPageServiceBridge.Settings.ActiveProfileId",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("_getActiveProfileId()", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsService.Instance", viewModel, StringComparison.Ordinal);
    }

    private static string ReadApplicationSource(string relativePath)
    {
        string path = Path.Combine(
            ApplicationRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing application source: {relativePath}");
        return File.ReadAllText(path);
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
}
