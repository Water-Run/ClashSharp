namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the Installer-owned update boundary in the desktop application.</summary>
public sealed class GitHubReleaseUpdateArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ReleaseChecker_IsReadOnlyAndUsesFixedClashSharpEndpoint()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "Service",
            "GitHubReleaseUpdateChecker.cs"));

        Assert.Contains(
            "https://api.github.com/repos/Water-Run/ClashSharp/releases/latest",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("browser_download_url", source, StringComparison.Ordinal);
        Assert.DoesNotContain("html_url", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsByteArrayAsync", source, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", source, StringComparison.Ordinal);
        Assert.Contains("Timeout = TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("MaximumResponseBytes = 64 * 1024", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutPage_OpensOnlyCompiledInLatestReleasePage()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "ClashSharp",
            "ViewModel",
            "AboutViewModel.cs"));

        Assert.Contains(
            "https://github.com/Water-Run/ClashSharp/releases/latest",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_launcher.LaunchAsync(result", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.slnx");
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, "ClashSharp");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ClashSharp source root.");
    }
}
