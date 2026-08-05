using System.Diagnostics;
using ClashSharp.Infrastructure.Processes;

namespace ClashSharp.MihomoService;

internal sealed record MihomoChildStartRequest(
    string MihomoPath,
    string WorkDirectory,
    string ConfigurationPath);

internal interface IMihomoChildProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    TextReader? StandardOutput { get; }

    TextReader? StandardError { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    Task StopTreeAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IMihomoChildProcessLauncher
{
    IMihomoChildProcess Start(MihomoChildStartRequest request);
}

/// <summary>Starts a suspended child, assigns it to a private kill-on-close Job, then resumes it.</summary>
internal sealed class WindowsMihomoChildProcessLauncher : IMihomoChildProcessLauncher
{
    internal const string ControllerPipeSddl = "D:P(A;;GA;;;SY)";

    private readonly WindowsJobProcessLauncher _launcher = new();

    public IMihomoChildProcess Start(MihomoChildStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyDictionary<string, string> environment = CreateSafeEnvironment(
            request.WorkDirectory);
        WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        try
        {
            WindowsJobProcess process = _launcher.Start(
                job,
                new WindowsJobProcessStartInfo(
                    request.MihomoPath,
                    request.WorkDirectory,
                    ["-d", request.WorkDirectory, "-f", request.ConfigurationPath],
                    CaptureOutput: true,
                    EnvironmentVariables: environment));
            return new WindowsMihomoChildProcess(job, process);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal static IReadOnlyDictionary<string, string> CreateSafeEnvironment(
        string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        string temporaryDirectory = Path.Combine(runtimeDirectory, "temp");
        Directory.CreateDirectory(temporaryDirectory);
        if ((File.GetAttributes(temporaryDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The mihomo temporary directory cannot be a reparse point.");
        }

        string windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolderOption.DoNotVerify);
        string systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(windowsDirectory)
            || string.IsNullOrWhiteSpace(systemDirectory))
        {
            throw new InvalidOperationException("Windows system directories are unavailable.");
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LISTEN_NAMEDPIPE_SDDL"] = ControllerPipeSddl,
            ["PATH"] = systemDirectory,
            ["SystemRoot"] = windowsDirectory,
            ["TEMP"] = temporaryDirectory,
            ["TMP"] = temporaryDirectory,
            ["WINDIR"] = windowsDirectory,
        };
    }
}

internal sealed class WindowsMihomoChildProcess : IMihomoChildProcess
{
    private readonly WindowsKillOnCloseJob _job;
    private readonly WindowsJobProcess _ownedProcess;
    private int _disposed;

    internal WindowsMihomoChildProcess(
        WindowsKillOnCloseJob job,
        WindowsJobProcess ownedProcess)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _ownedProcess = ownedProcess ?? throw new ArgumentNullException(nameof(ownedProcess));
    }

    private Process Process => _ownedProcess.Process;

    public int Id => Process.Id;

    public bool HasExited => Process.HasExited;

    public int? ExitCode => Process.HasExited ? Process.ExitCode : null;

    public TextReader? StandardOutput => _ownedProcess.StandardOutput;

    public TextReader? StandardError => _ownedProcess.StandardError;

    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        return Process.WaitForExitAsync(cancellationToken);
    }

    public async Task StopTreeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _job.TerminateAndWaitForEmptyAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!Process.HasExited)
        {
            await Process.WaitForExitAsync(cancellationToken)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Closing the Job first activates KILL_ON_JOB_CLOSE even if a preceding graceful
        // termination/confirmation path failed during service shutdown.
        _job.Dispose();
        _ownedProcess.Dispose();
    }
}
