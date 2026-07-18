using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies mihomo process startup diagnostics.</summary>
public sealed class MihomoCoreServiceTests
{
    /// <summary>Verifies an early process exit drains both streams before rendering the bounded diagnostic.</summary>
    [Fact]
    public void Start_WhenProcessExitsEarly_WaitsForBothStreamsBeforeThrowing()
    {
        string probePath = FindProbeExecutablePath();
        string testRoot = Path.Combine(Path.GetTempPath(), "ClashSharp", "CoreProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        string configPath = Path.Combine(testRoot, "config.yaml");
        File.WriteAllText(configPath, "mixed-port: 7890");
        MihomoCoreService service = new(probePath, TimeSpan.FromSeconds(5));

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                service.Start(new CoreConfigurationState(testRoot, configPath, true)));

            Assert.Contains("core-out-final", exception.Message, StringComparison.Ordinal);
            Assert.Contains("core-err-final", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Text.StringBuilder", exception.Message, StringComparison.Ordinal);
            Assert.False(service.IsRunning);
        }
        finally
        {
            service.Stop();
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string FindProbeExecutablePath()
    {
        string repositoryRoot = FindRepositoryRoot();
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        bool usesPlatformOutput = AppContext.BaseDirectory
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Contains("x64", StringComparer.OrdinalIgnoreCase);
        List<string> pathParts =
        [
            repositoryRoot,
            "ClashSharp",
            "ClashSharp.ProcessProbe",
            "bin",
        ];
        if (usesPlatformOutput)
        {
            pathParts.Add("x64");
        }

        pathParts.Add(configuration);
        pathParts.Add("net10.0");
        pathParts.Add("ClashSharp.ProcessProbe.exe");
        string path = Path.Combine([.. pathParts]);
        Assert.True(File.Exists(path), $"Process probe executable was not built: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClashSharp", "ClashSharp.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output.");
    }
}
