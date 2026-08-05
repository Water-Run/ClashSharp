using System.Diagnostics;
using System.Text;
using ClashSharp.Infrastructure.Processes;
using ClashSharp.MihomoService;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies mihomo process startup diagnostics.</summary>
public sealed class MihomoCoreServiceTests
{
    [Fact]
    public async Task GetVersionTextAsync_WhenCallerCancels_CompletesNonCancelableJobEmptyHandoff()
    {
        string probePath = FindProbeExecutablePath();
        RecordingTerminationJob? probeJob = null;
        IWindowsProcessJob CreateJob()
        {
            probeJob = new RecordingTerminationJob(WindowsKillOnCloseJob.Create());
            return probeJob;
        }

        MihomoCoreService service = new(
            probePath,
            TimeSpan.FromMilliseconds(50),
            processJobFactory: CreateJob);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetVersionTextAsync(cancellation.Token));

        Assert.NotNull(probeJob);
        Assert.Equal(1, probeJob.TerminationAttempts);
        Assert.False(probeJob.ObservedCancellationToken.CanBeCanceled);
    }

    [Fact]
    public void Stop_TerminatesEntireGenerationAndCreatesFreshJobForReplacement()
    {
        string probePath = FindProbeExecutablePath();
        string testRoot = CreateProbeRoot("process-probe: spawn-child");
        string configPath = Path.Combine(testRoot, "config.yaml");
        int createdJobs = 0;
        IWindowsProcessJob CreateJob()
        {
            createdJobs++;
            return WindowsKillOnCloseJob.Create();
        }

        MihomoCoreService service = new(
            probePath,
            TimeSpan.FromMilliseconds(50),
            processJobFactory: CreateJob);
        Process? firstChild = null;
        Process? secondChild = null;

        try
        {
            service.Start(new CoreConfigurationState(testRoot, configPath, true));
            MihomoAppProcessIdentity firstIdentity = AssertAppProcessIdentity(service);
            firstChild = GetReportedChild(testRoot);

            service.Stop();

            Assert.True(firstChild.WaitForExit(5000));
            Assert.Null(service.CaptureAppProcessIdentity());
            Assert.False(service.IsCurrentAppProcessIdentity(firstIdentity));
            File.Delete(Path.Combine(testRoot, "child.pid"));
            service.Start(new CoreConfigurationState(testRoot, configPath, true));
            MihomoAppProcessIdentity secondIdentity = AssertAppProcessIdentity(service);
            secondChild = GetReportedChild(testRoot);
            Assert.Equal(2, createdJobs);
            Assert.NotEqual(firstIdentity.Epoch, secondIdentity.Epoch);
            Assert.False(service.IsCurrentAppProcessIdentity(firstIdentity));

            service.Stop();

            Assert.True(secondChild.WaitForExit(5000));
            Assert.False(service.HasOwnershipFault);
        }
        finally
        {
            service.Stop();
            firstChild?.Dispose();
            secondChild?.Dispose();
            DeleteProbeRoot(testRoot);
        }
    }

    private static MihomoAppProcessIdentity AssertAppProcessIdentity(MihomoCoreService service)
    {
        MihomoAppProcessIdentity? captured = service.CaptureAppProcessIdentity();
        Assert.NotNull(captured);
        MihomoAppProcessIdentity identity = captured.Value;
        Assert.NotEqual(Guid.Empty, identity.Epoch);
        Assert.True(identity.RootProcessId > 0);
        Assert.True(service.IsCurrentAppProcessIdentity(identity));
        return identity;
    }

    [Fact]
    public async Task UnexpectedRootExit_TerminatesDescendantBeforePublishingOwnershipRelease()
    {
        string probePath = FindProbeExecutablePath();
        string testRoot = CreateProbeRoot("process-probe: spawn-child-then-exit");
        string configPath = Path.Combine(testRoot, "config.yaml");
        MihomoCoreService service = new(probePath, TimeSpan.FromMilliseconds(50));
        TaskCompletionSource<MihomoCoreUnexpectedExitEventArgs> exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.UnexpectedExit += (_, eventArgs) => exited.TrySetResult(eventArgs);
        Process? child = null;

        try
        {
            service.Start(new CoreConfigurationState(testRoot, configPath, true));
            child = GetReportedChild(testRoot);

            MihomoCoreUnexpectedExitEventArgs eventArgs = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(42, eventArgs.ExitCode);
            Assert.True(child.HasExited);
            Assert.False(service.IsRunning);
            Assert.False(service.HasOwnershipFault);
        }
        finally
        {
            service.Stop();
            child?.Dispose();
            DeleteProbeRoot(testRoot);
        }
    }

    [Fact]
    public void Stop_WhenJobEmptyProofFails_RetainsFaultAndBlocksReplacementUntilRetrySucceeds()
    {
        string probePath = FindProbeExecutablePath();
        string testRoot = CreateProbeRoot("process-probe: spawn-child");
        string configPath = Path.Combine(testRoot, "config.yaml");
        int createdJobs = 0;
        FlakyTerminationJob? failedJob = null;
        IWindowsProcessJob CreateJob()
        {
            createdJobs++;
            WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
            if (createdJobs != 1)
            {
                return job;
            }

            failedJob = new FlakyTerminationJob(job);
            return failedJob;
        }

        MihomoCoreService service = new(
            probePath,
            TimeSpan.FromMilliseconds(50),
            processJobFactory: CreateJob);

        try
        {
            service.Start(new CoreConfigurationState(testRoot, configPath, true));

            InvalidOperationException stopFailure = Assert.Throws<InvalidOperationException>(service.Stop);

            Assert.Contains("did not become empty", stopFailure.Message, StringComparison.Ordinal);
            Assert.True(service.HasOwnershipFault);
            Assert.Equal(1, createdJobs);
            Assert.Throws<InvalidOperationException>(() =>
                service.Start(new CoreConfigurationState(testRoot, configPath, true)));
            Assert.Equal(1, createdJobs);

            service.Stop();
            Assert.Equal(2, failedJob!.TerminationAttempts);
            Assert.False(service.HasOwnershipFault);

            service.Start(new CoreConfigurationState(testRoot, configPath, true));
            Assert.Equal(2, createdJobs);
        }
        finally
        {
            service.Stop();
            DeleteProbeRoot(testRoot);
        }
    }

    [Fact]
    public void Start_WhenRootFailsDuringObservation_TerminatesDescendantBeforeAllowingReplacement()
    {
        string probePath = FindProbeExecutablePath();
        string testRoot = CreateProbeRoot("process-probe: spawn-child-startup-failure");
        string configPath = Path.Combine(testRoot, "config.yaml");
        MihomoCoreService service = new(probePath, TimeSpan.FromSeconds(5));

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                service.Start(new CoreConfigurationState(testRoot, configPath, true)));

            AssertProcessExited(ReadReportedChildId(testRoot));
            Assert.False(service.HasOwnershipFault);

            File.Delete(Path.Combine(testRoot, "child.pid"));
            File.WriteAllText(configPath, "process-probe: spawn-child");
            service.Start(new CoreConfigurationState(testRoot, configPath, true));
            Assert.True(service.IsRunning);
        }
        finally
        {
            service.Stop();
            DeleteProbeRoot(testRoot);
        }
    }

    [Fact]
    public async Task Start_WhenOwnedProcessExitsAfterStartup_RaisesUnexpectedExitAndClearsOwnership()
    {
        string probePath = FindProbeExecutablePath();
        string testRoot = Path.Combine(Path.GetTempPath(), "ClashSharp", "CoreProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        string configPath = Path.Combine(testRoot, "config.yaml");
        File.WriteAllText(configPath, "process-probe: delayed-exit");
        MihomoCoreService service = new(probePath, TimeSpan.FromMilliseconds(50));
        TaskCompletionSource<MihomoCoreUnexpectedExitEventArgs> exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.UnexpectedExit += (_, eventArgs) => exited.TrySetResult(eventArgs);

        try
        {
            service.Start(new CoreConfigurationState(testRoot, configPath, true));
            Assert.True(service.IsRunning);

            MihomoCoreUnexpectedExitEventArgs eventArgs = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(42, eventArgs.ExitCode);
            Assert.False(service.IsRunning);
        }
        finally
        {
            service.Stop();
            Directory.Delete(testRoot, recursive: true);
        }
    }

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

    [Fact]
    public void AtomicLauncher_WhenJobOwnerCloses_TerminatesRootAndDescendant()
    {
        string probePath = FindProbeExecutablePath();
        WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        WindowsJobProcess? launched = null;
        Process? child = null;

        try
        {
            launched = new WindowsJobProcessLauncher().Start(
                job,
                new WindowsJobProcessStartInfo(
                    probePath,
                    Path.GetDirectoryName(probePath)!,
                    ["spawn-child"],
                    CaptureOutput: true));
            int childProcessId = ReadChildProcessId(launched.StandardOutput!);
            child = Process.GetProcessById(childProcessId);

            job.Dispose();

            Assert.True(launched.Process.WaitForExit(5000));
            Assert.True(child.WaitForExit(5000));
        }
        finally
        {
            job.Dispose();
            launched?.Dispose();
            child?.Dispose();
        }
    }

    [Fact]
    public void AtomicLauncher_WhenAssignmentFails_TerminatesSuspendedChild()
    {
        string probePath = FindProbeExecutablePath();
        int suspendedProcessId = 0;
        WindowsJobProcessLauncher launcher = new((stage, processId) =>
        {
            if (stage == WindowsJobProcessLaunchStage.BeforeAssignment)
            {
                suspendedProcessId = processId;
                throw new InjectedProcessLaunchException("assignment");
            }
        });
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();

        InjectedProcessLaunchException exception = Assert.Throws<InjectedProcessLaunchException>(() =>
            launcher.Start(
                job,
                new WindowsJobProcessStartInfo(
                    probePath,
                    Path.GetDirectoryName(probePath)!,
                    ["hang"],
                    CaptureOutput: false)));

        Assert.Equal("assignment", exception.Message);
        AssertProcessExited(suspendedProcessId);
    }

    [Fact]
    public void AtomicLauncher_WhenResumeFails_TerminatesJobOwnedSuspendedChild()
    {
        string probePath = FindProbeExecutablePath();
        int suspendedProcessId = 0;
        WindowsJobProcessLauncher launcher = new((stage, processId) =>
        {
            if (stage == WindowsJobProcessLaunchStage.BeforeResume)
            {
                suspendedProcessId = processId;
                throw new InjectedProcessLaunchException("resume");
            }
        });
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();

        InjectedProcessLaunchException exception = Assert.Throws<InjectedProcessLaunchException>(() =>
            launcher.Start(
                job,
                new WindowsJobProcessStartInfo(
                    probePath,
                    Path.GetDirectoryName(probePath)!,
                    ["hang"],
                    CaptureOutput: false)));

        Assert.Equal("resume", exception.Message);
        AssertProcessExited(suspendedProcessId);
    }

    [Fact]
    public async Task AtomicLauncher_CapturedOutputAndQuotedArguments_RoundTripExactly()
    {
        string probePath = FindProbeExecutablePath();
        string[] arguments = [string.Empty, "space value", "quote\"value", @"trailing\\"];
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using WindowsJobProcess launched = new WindowsJobProcessLauncher().Start(
            job,
            new WindowsJobProcessStartInfo(
                probePath,
                Path.GetDirectoryName(probePath)!,
                ["arguments", .. arguments],
                CaptureOutput: true));

        string output = await launched.StandardOutput!.ReadToEndAsync();
        string error = await launched.StandardError!.ReadToEndAsync();
        await launched.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        string[] actual = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line[4..])))
            .ToArray();
        Assert.Equal(arguments, actual);
        Assert.Empty(error);
        Assert.Equal(0, launched.Process.ExitCode);
    }

    [Fact]
    public async Task AtomicLauncher_ExplicitEnvironmentFilter_RemovesMihomoSafetyOverrides()
    {
        const string skipSafePathCheck = "SKIP_SAFE_PATH_CHECK";
        const string safePaths = "SAFE_PATHS";
        string? previousSkip = Environment.GetEnvironmentVariable(skipSafePathCheck);
        string? previousSafePaths = Environment.GetEnvironmentVariable(safePaths);
        string probePath = FindProbeExecutablePath();
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        WindowsJobProcess? launched = null;
        try
        {
            Environment.SetEnvironmentVariable(skipSafePathCheck, "1");
            Environment.SetEnvironmentVariable(safePaths, @"C:\Users\attacker");
            launched = new WindowsJobProcessLauncher().Start(
                job,
                new WindowsJobProcessStartInfo(
                    probePath,
                    Path.GetDirectoryName(probePath)!,
                    ["environment", skipSafePathCheck, safePaths],
                    CaptureOutput: true,
                    EnvironmentVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(skipSafePathCheck, previousSkip);
            Environment.SetEnvironmentVariable(safePaths, previousSafePaths);
        }

        Assert.NotNull(launched);
        using (launched)
        {
            string output = await launched.StandardOutput!.ReadToEndAsync();
            await launched.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            string[] values = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line[4..])))
                .ToArray();
            Assert.Equal(["<missing>", "<missing>"], values);
            Assert.Equal(0, launched.Process.ExitCode);
        }

    }

    [Fact]
    public async Task AtomicLauncher_WithoutOutputCapture_UsesPlainStartupInfoAndRunsChild()
    {
        string probePath = FindProbeExecutablePath();
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using WindowsJobProcess launched = new WindowsJobProcessLauncher().Start(
            job,
            new WindowsJobProcessStartInfo(
                probePath,
                Path.GetDirectoryName(probePath)!,
                ["emit", "0", "31"],
                CaptureOutput: false));

        await launched.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(launched.StandardOutput);
        Assert.Null(launched.StandardError);
        Assert.Equal(31, launched.Process.ExitCode);
    }

    private static int ReadChildProcessId(StreamReader output)
    {
        for (int lineIndex = 0; lineIndex < 10; lineIndex++)
        {
            string line = output.ReadLine() ?? throw new EndOfStreamException("The process tree probe exited early.");
            if (line.StartsWith("child:", StringComparison.Ordinal))
            {
                return int.Parse(line.AsSpan("child:".Length), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException("The process tree probe did not report its child process identifier.");
    }

    private static string CreateProbeRoot(string configuration)
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "ClashSharp", "CoreProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        File.WriteAllText(Path.Combine(testRoot, "config.yaml"), configuration);
        return testRoot;
    }

    private static Process GetReportedChild(string testRoot)
    {
        return Process.GetProcessById(ReadReportedChildId(testRoot));
    }

    private static int ReadReportedChildId(string testRoot)
    {
        string childPath = Path.Combine(testRoot, "child.pid");
        int processId = 0;
        Assert.True(
            SpinWait.SpinUntil(
                () => TryReadProcessId(childPath, out processId),
                TimeSpan.FromSeconds(5)),
            "The core probe did not publish its descendant process identifier.");
        return processId;
    }

    private static bool TryReadProcessId(string path, out int processId)
    {
        processId = 0;
        try
        {
            return File.Exists(path)
                && int.TryParse(
                    File.ReadAllText(path),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out processId)
                && processId > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void DeleteProbeRoot(string path)
    {
        Exception? lastFailure = null;
        bool deleted = SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return true;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    lastFailure = exception;
                    return false;
                }
            },
            TimeSpan.FromSeconds(5));
        Assert.True(deleted, lastFailure?.Message ?? "The process probe directory could not be deleted.");
    }

    private static void AssertProcessExited(int processId)
    {
        Assert.True(processId > 0);
        try
        {
            using Process process = Process.GetProcessById(processId);
            Assert.True(process.WaitForExit(5000));
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed class InjectedProcessLaunchException(string message) : Exception(message);

    private sealed class FlakyTerminationJob(IWindowsProcessJob inner) : IWindowsProcessJob
    {
        public int TerminationAttempts { get; private set; }

        public void AssignProcess(Microsoft.Win32.SafeHandles.SafeFileHandle processHandle)
        {
            inner.AssignProcess(processHandle);
        }

        public void TerminateAndWaitForEmpty(TimeSpan timeout)
        {
            TerminationAttempts++;
            if (TerminationAttempts == 1)
            {
                throw new IOException("Injected Job accounting failure.");
            }

            inner.TerminateAndWaitForEmpty(timeout);
        }

        public Task TerminateAndWaitForEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            return inner.TerminateAndWaitForEmptyAsync(timeout, cancellationToken);
        }

        public void Dispose()
        {
            inner.Dispose();
        }
    }

    private sealed class RecordingTerminationJob(IWindowsProcessJob inner) : IWindowsProcessJob
    {
        public int TerminationAttempts { get; private set; }

        public CancellationToken ObservedCancellationToken { get; private set; }

        public void AssignProcess(Microsoft.Win32.SafeHandles.SafeFileHandle processHandle)
        {
            inner.AssignProcess(processHandle);
        }

        public void TerminateAndWaitForEmpty(TimeSpan timeout)
        {
            TerminationAttempts++;
            inner.TerminateAndWaitForEmpty(timeout);
        }

        public Task TerminateAndWaitForEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            TerminationAttempts++;
            ObservedCancellationToken = cancellationToken;
            return inner.TerminateAndWaitForEmptyAsync(timeout, cancellationToken);
        }

        public void Dispose()
        {
            inner.Dispose();
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
