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
    public void GovernedViews_RequireExplicitFactoryOwnedDependencies()
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
            Assert.DoesNotMatch(
                new Regex(
                    $@"public\s+{pageName}\s*\(\s*\)",
                    RegexOptions.CultureInvariant),
                source);
            Assert.DoesNotContain($"{pageName}PageComposition.Create(", source, StringComparison.Ordinal);
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
            Assert.Contains("PageCompositionContext context", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".Instance", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.UI.Xaml", source, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies the legacy presentation service locator is deleted rather than renamed.</summary>
    [Fact]
    public void LegacyPageServiceBridge_IsDeletedAndPresentationHasNoServiceLocator()
    {
        string bridgePath = Path.Combine(
            ApplicationRoot,
            "Presentation",
            "Composition",
            "LegacyPageServiceBridge.cs");
        Assert.False(File.Exists(bridgePath));

        string[] offenders = Directory
            .EnumerateFiles(ApplicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(ApplicationRoot, path).Replace('\\', '/');
                return relative.StartsWith("Presentation/", StringComparison.Ordinal)
                    || relative.StartsWith("View/", StringComparison.Ordinal)
                    || relative.StartsWith("ViewModel/", StringComparison.Ordinal);
            })
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                @"\b[A-Za-z_][A-Za-z0-9_]*Service\.Instance\b",
                RegexOptions.CultureInvariant))
            .Select(path => Path.GetRelativePath(ApplicationRoot, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>Verifies profile fallback state crosses the composition boundary explicitly.</summary>
    [Fact]
    public void Profiles_InjectsActiveProfileFallback()
    {
        string composition = ReadApplicationSource(
            "Presentation/Composition/ProfilesPageComposition.cs");
        string viewModel = ReadApplicationSource("ViewModel/ProfilesViewModel.cs");

        Assert.Contains(
            "() => context.Settings.ActiveProfileId",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("_getActiveProfileId()", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsService.Instance", viewModel, StringComparison.Ordinal);
    }

    /// <summary>Verifies About owns a cancellable load session for remote update checks.</summary>
    [Fact]
    public void About_UsesSymmetricCancellablePageLifetime()
    {
        string source = ReadApplicationSource("View/About.xaml.cs");

        Assert.Contains("private readonly PageLoadSession _loadSession = new();", source, StringComparison.Ordinal);
        Assert.Contains("Loaded += OnLoaded;", source, StringComparison.Ordinal);
        Assert.Contains("Unloaded += OnUnloaded;", source, StringComparison.Ordinal);
        Assert.Contains("await _loadSession.RunAsync(_viewModel.LoadAsync);", source, StringComparison.Ordinal);
        Assert.Contains("_loadSession.Cancel();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadCommand.Execute", source, StringComparison.Ordinal);
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
