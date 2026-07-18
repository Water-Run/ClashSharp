using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClashSharp.ApplicationModel.Processes;
using ClashSharp.Infrastructure.Processes;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies real Windows process execution, stream draining, and process-tree cleanup.</summary>
public sealed class WindowsProcessRunnerTests
{
    /// <summary>Verifies both redirected streams are drained concurrently and non-zero exit remains typed completion.</summary>
    [Fact]
    public async Task RunAsync_WithConcurrentOutput_ReturnsCompleteStreamsAndExitCode()
    {
        const int lineCount = 12000;
        WindowsProcessRunner runner = new();
        ProcessRequest request = CreateProbeRequest(
            TimeSpan.FromSeconds(15),
            "emit",
            lineCount.ToString(CultureInfo.InvariantCulture),
            "7");

        ProcessRunResult result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessRunOutcome.Completed, result.Outcome);
        Assert.Equal(7, result.ExitCode);
        Assert.True(result.ProcessId > 0);
        Assert.Equal(lineCount, ReadLines(result.StandardOutput).Length);
        Assert.Equal(lineCount, ReadLines(result.StandardError).Length);
        Assert.Contains("out:00000", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("out:11999", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("err:00000", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("err:11999", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>Verifies spaces, quotes, trailing slashes, and empty values remain individual arguments.</summary>
    [Fact]
    public async Task RunAsync_WithComplexArguments_PreservesArgumentBoundaries()
    {
        string[] arguments = ["value with spaces", "quote\"value", "trailing\\", string.Empty];
        WindowsProcessRunner runner = new();
        ProcessRequest request = CreateProbeRequest(
            TimeSpan.FromSeconds(10),
            ["arguments", .. arguments]);

        ProcessRunResult result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            arguments.Select(value => "arg:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value))),
            ReadLines(result.StandardOutput));
    }

    /// <summary>Verifies process start failures are typed and do not invent an exit code.</summary>
    [Fact]
    public async Task RunAsync_WhenExecutableIsMissing_ReturnsStartFailed()
    {
        WindowsProcessRunner runner = new();
        ProcessRequest request = new(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
            [],
            TimeSpan.FromSeconds(5));

        ProcessRunResult result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessRunOutcome.StartFailed, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Equal(0, result.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    /// <summary>Verifies timeout is typed, bounded, and kills both the probe and its child.</summary>
    [Fact]
    public async Task RunAsync_WhenTimedOut_KillsEntireProcessTreeAndDrainsOutput()
    {
        WindowsProcessRunner runner = new();
        ProcessRequest request = CreateProbeRequest(TimeSpan.FromMilliseconds(750), "spawn-child");
        Stopwatch stopwatch = Stopwatch.StartNew();

        ProcessRunResult result = await runner.RunAsync(request, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(ProcessRunOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        int childProcessId = ParseProcessId(result.StandardOutput, "child:");
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(8));
        await AssertProcessExitedAsync(result.ProcessId);
        await AssertProcessExitedAsync(childProcessId);
    }

    /// <summary>Verifies caller cancellation is a distinct result and performs the same tree cleanup.</summary>
    [Fact]
    public async Task RunAsync_WhenCallerCancels_ReturnsCancelledAndKillsEntireProcessTree()
    {
        WindowsProcessRunner runner = new();
        ProcessRequest request = CreateProbeRequest(TimeSpan.FromSeconds(30), "spawn-child");
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(750));
        Stopwatch stopwatch = Stopwatch.StartNew();

        ProcessRunResult result = await runner.RunAsync(request, cancellation.Token);
        stopwatch.Stop();

        Assert.Equal(ProcessRunOutcome.Cancelled, result.Outcome);
        Assert.Null(result.ExitCode);
        int childProcessId = ParseProcessId(result.StandardOutput, "child:");
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(8));
        await AssertProcessExitedAsync(result.ProcessId);
        await AssertProcessExitedAsync(childProcessId);
    }

    private static ProcessRequest CreateProbeRequest(TimeSpan timeout, params string[] probeArguments)
    {
        string probePath = FindProbePath();
        return new ProcessRequest(
            "dotnet",
            [probePath, .. probeArguments],
            timeout);
    }

    private static string FindProbePath()
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
        pathParts.Add("ClashSharp.ProcessProbe.dll");
        string path = Path.Combine([.. pathParts]);
        Assert.True(File.Exists(path), $"Process probe was not built: {path}");
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

    private static string[] ReadLines(string text)
    {
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    private static int ParseProcessId(string output, string prefix)
    {
        string line = Assert.Single(ReadLines(output), item => item.StartsWith(prefix, StringComparison.Ordinal));
        return int.Parse(line[prefix.Length..], CultureInfo.InvariantCulture);
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (IsProcessRunning(processId))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
