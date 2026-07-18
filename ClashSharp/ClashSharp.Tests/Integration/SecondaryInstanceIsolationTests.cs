using System.Diagnostics;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies cross-process arbitration prevents secondary host and shared-state side effects.</summary>
public sealed class SecondaryInstanceIsolationTests
{
    /// <summary>Starts two helper processes and proves only the primary constructs and starts a host.</summary>
    [Fact]
    public async Task SecondaryProcess_DoesNotConstructHostOrWriteMutationMarker()
    {
        string repositoryRoot = FindRepositoryRoot();
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        string probePath = Path.Combine(
            repositoryRoot,
            "ClashSharp",
            "ClashSharp.StartupProbe",
            "bin",
            "x64",
            configuration,
            "net10.0",
            "ClashSharp.StartupProbe.dll");
        Assert.True(File.Exists(probePath), $"Startup probe was not built: {probePath}");

        string testRoot = Path.Combine(Path.GetTempPath(), "ClashSharp", "StartupIsolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        string tracePath = Path.Combine(testRoot, "trace.log");
        string readyPath = Path.Combine(testRoot, "primary.ready");
        string releasePath = Path.Combine(testRoot, "primary.release");
        string instanceKey = Guid.NewGuid().ToString("N");
        Process? primary = null;
        Process? secondary = null;

        try
        {
            primary = StartProbe(probePath, instanceKey, tracePath, readyPath, releasePath);
            await WaitForFileAsync(readyPath, TimeSpan.FromSeconds(10));

            secondary = StartProbe(probePath, instanceKey, tracePath, readyPath, releasePath);
            await WaitForExitAsync(secondary, TimeSpan.FromSeconds(10));
            string secondaryError = await secondary.StandardError.ReadToEndAsync();
            Assert.True(secondary.ExitCode == 0, $"Secondary exited with {secondary.ExitCode}: {secondaryError}");

            await File.WriteAllTextAsync(releasePath, "release");
            await WaitForExitAsync(primary, TimeSpan.FromSeconds(10));
            string primaryError = await primary.StandardError.ReadToEndAsync();
            Assert.True(primary.ExitCode == 0, $"Primary exited with {primary.ExitCode}: {primaryError}");

            string[] trace = await File.ReadAllLinesAsync(tracePath);
            Assert.Single(trace, line => line.StartsWith("host-build:", StringComparison.Ordinal));
            Assert.Single(trace, line => line.StartsWith("host-start:", StringComparison.Ordinal));
            Assert.Single(trace, line => line.StartsWith("secondary-redirected:", StringComparison.Ordinal));
            Assert.DoesNotContain(trace, line => line.StartsWith("secondary-mutation:", StringComparison.Ordinal));
        }
        finally
        {
            KillIfRunning(secondary);
            KillIfRunning(primary);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static Process StartProbe(
        string probePath,
        string instanceKey,
        string tracePath,
        string readyPath,
        string releasePath)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(probePath);
        startInfo.ArgumentList.Add("--instance-key");
        startInfo.ArgumentList.Add(instanceKey);
        startInfo.ArgumentList.Add("--trace");
        startInfo.ArgumentList.Add(tracePath);
        startInfo.ArgumentList.Add("--ready");
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add("--release");
        startInfo.ArgumentList.Add(releasePath);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the startup probe process.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using CancellationTokenSource timeoutSource = new(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(25, timeoutSource.Token);
        }
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using CancellationTokenSource timeoutSource = new(timeout);
        await process.WaitForExitAsync(timeoutSource.Token);
    }

    private static void KillIfRunning(Process? process)
    {
        if (process is null)
        {
            return;
        }

        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
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
