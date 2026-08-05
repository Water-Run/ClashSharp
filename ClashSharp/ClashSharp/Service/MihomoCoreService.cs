using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Infrastructure.Processes;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides access to the bundled mihomo core binary and its runtime process state.</summary>
/// <remarks>
/// Invariants: <see cref="BinaryPath"/> always points to the expected bundled core location.
/// Thread safety: Public process state reads are guarded by a private lock.
/// Side effects: Version probing starts a short-lived mihomo process.
/// </remarks>
public sealed class MihomoCoreService
{
    private const int StartupDiagnosticCapacity = 4096;

    private static readonly TimeSpan DefaultStartupObservationWindow = TimeSpan.FromMilliseconds(1200);

    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="MihomoCoreService"/> instance.</value>
    public static MihomoCoreService Instance { get; } = new();

    /// <summary>Synchronization object guarding mutable process state for this service lifetime.</summary>
    private readonly object _syncLock = new();

    private readonly int _startupObservationMilliseconds;

    /// <summary>Current long-running mihomo process owned by Clash#; null when not started.</summary>
    private Process? _process;

    /// <summary>Unforgeable lifetime marker for the currently committed App-owned root process.</summary>
    private Guid _processEpoch;

    /// <summary>Blocks ownership handoff when a resumed child could not be confirmed terminated.</summary>
    private Exception? _processOwnershipFault;

    /// <summary>False when a failed pre-Job launch lost the only handle needed to re-prove exit.</summary>
    private bool _processOwnershipFaultIsRecoverable = true;

    /// <summary>Kill-on-owner-close job containing every App-owned mihomo child.</summary>
    private IWindowsProcessJob? _processJob;

    /// <summary>Creates children suspended so Job ownership is committed before their first instruction.</summary>
    private readonly WindowsJobProcessLauncher _processLauncher;

    /// <summary>Creates a fresh Job Object for each App-owned runtime generation.</summary>
    private readonly Func<IWindowsProcessJob> _processJobFactory;

    /// <summary>Owns redirected streams and the managed handle for the current long-running child.</summary>
    private WindowsJobProcess? _ownedProcess;

    /// <summary>Owns both synchronous pipe-reader threads for the current runtime generation.</summary>
    private SynchronousProcessOutputDrain? _outputDrain;

    /// <summary>Prevents a replacement listener from committing before crash rollback finishes.</summary>
    private bool _crashRecoveryInProgress;

    /// <summary>Blocks replacement ownership until a failed WinINet crash rollback is retried.</summary>
    private Exception? _crashNetworkRecoveryFailure;

    /// <summary>Raised when the App-owned long-running core exits without an explicit stop.</summary>
    internal event EventHandler<MihomoCoreUnexpectedExitEventArgs>? UnexpectedExit;

    /// <summary>Initializes the core service and resolves the bundled binary path.</summary>
    private MihomoCoreService()
        : this(
            Path.Combine(AppContext.BaseDirectory, "Binaries", "mihomo.exe"),
            DefaultStartupObservationWindow)
    {
    }

    /// <summary>Initializes a core service with testable process inputs.</summary>
    internal MihomoCoreService(
        string binaryPath,
        TimeSpan startupObservationWindow,
        WindowsJobProcessLauncher? processLauncher = null,
        Func<IWindowsProcessJob>? processJobFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        if (startupObservationWindow <= TimeSpan.Zero
            || startupObservationWindow.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(startupObservationWindow));
        }

        BinaryPath = binaryPath;
        _startupObservationMilliseconds = checked((int)Math.Ceiling(startupObservationWindow.TotalMilliseconds));
        _processLauncher = processLauncher ?? new WindowsJobProcessLauncher();
        _processJobFactory = processJobFactory ?? WindowsKillOnCloseJob.Create;
    }

    /// <summary>Gets the expected bundled mihomo binary path.</summary>
    /// <value>Absolute path under the application base directory; never null.</value>
    public string BinaryPath { get; }

    /// <summary>Gets whether the bundled mihomo binary currently exists on disk.</summary>
    /// <value>True when <see cref="BinaryPath"/> exists; otherwise false.</value>
    public bool IsBinaryAvailable => File.Exists(BinaryPath);

    /// <summary>Gets whether a long-running mihomo process owned by this service is currently active.</summary>
    /// <value>True when the owned process exists and has not exited; otherwise false.</value>
    public bool IsRunning
    {
        get
        {
            lock (_syncLock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>Captures the exact live Job root used to authenticate App-owned listeners.</summary>
    internal MihomoAppProcessIdentity? CaptureAppProcessIdentity()
    {
        lock (_syncLock)
        {
            return TryGetCurrentProcessIdentityUnderLock(out MihomoAppProcessIdentity identity)
                ? identity
                : null;
        }
    }

    /// <summary>Revalidates a previously captured root after a potentially racing native operation.</summary>
    internal bool IsCurrentAppProcessIdentity(MihomoAppProcessIdentity identity)
    {
        lock (_syncLock)
        {
            return TryGetCurrentProcessIdentityUnderLock(out MihomoAppProcessIdentity current)
                && current == identity;
        }
    }

    /// <summary>Gets whether process or crash-network ownership is in an unresolved fault state.</summary>
    internal bool HasOwnershipFault
    {
        get
        {
            lock (_syncLock)
            {
                return _processOwnershipFault is not null || _crashNetworkRecoveryFailure is not null;
            }
        }
    }

    /// <summary>Runs the bundled mihomo binary with the version flag and returns its first output line.</summary>
    /// <param name="cancellationToken">Cancels the probe and terminates the probe process when cancellation is requested.</param>
    /// <returns>The first non-empty version output line from mihomo.</returns>
    /// <exception cref="FileNotFoundException">The bundled mihomo binary does not exist at <see cref="BinaryPath"/>.</exception>
    /// <exception cref="InvalidOperationException">The version probe cannot start or exits without version output.</exception>
    /// <remarks>
    /// Cancellation semantics: Cancellation terminates only the short-lived probe process.
    /// Completion semantics: The method starts a new process on each call and does not mutate long-running core state.
    /// </remarks>
    public async Task<string> GetVersionTextAsync(CancellationToken cancellationToken)
    {
        if (!IsBinaryAvailable)
        {
            throw new FileNotFoundException("Bundled mihomo core was not found.", BinaryPath);
        }

        using IWindowsProcessJob probeJob = _processJobFactory();
        using WindowsJobProcess ownedProcess = _processLauncher.Start(
            probeJob,
            new WindowsJobProcessStartInfo(
                BinaryPath,
                AppContext.BaseDirectory,
                ["-v"],
                CaptureOutput: true));
        Process process = ownedProcess.Process;
        StreamReader standardOutput = ownedProcess.StandardOutput
            ?? throw new InvalidOperationException("The mihomo version output pipe was not created.");
        StreamReader standardError = ownedProcess.StandardError
            ?? throw new InvalidOperationException("The mihomo version error pipe was not created.");

        try
        {
            Task<string> outputTask = standardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = standardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await probeJob.TerminateAndWaitForEmptyAsync(
                    ProcessExitTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);

            string output = (await outputTask.ConfigureAwait(false)).Trim();
            string error = (await errorTask.ConfigureAwait(false)).Trim();
            string text = string.IsNullOrWhiteSpace(output) ? error : output;
            string[] lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return lines.Length > 0
                ? lines[0]
                : throw new InvalidOperationException("The bundled mihomo core exited without version output.");
        }
        catch (OperationCanceledException cancellationFailure)
        {
            try
            {
                await probeJob.TerminateAndWaitForEmptyAsync(
                        ProcessExitTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "The canceled mihomo version probe Job could not be confirmed empty.",
                    cancellationFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
            throw;
        }
    }

    /// <summary>Starts the bundled mihomo core with the managed local configuration when it is not already running.</summary>
    /// <param name="configurationState">Managed configuration state whose file must exist.</param>
    /// <exception cref="FileNotFoundException">The bundled core binary or configuration file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The core process cannot be started.</exception>
    public void Start(CoreConfigurationState configurationState)
    {
        if (!IsBinaryAvailable)
        {
            throw new FileNotFoundException("Bundled mihomo core was not found.", BinaryPath);
        }

        if (!configurationState.Exists)
        {
            throw new FileNotFoundException("Managed mihomo configuration was not found.", configurationState.ConfigPath);
        }

        lock (_syncLock)
        {
            WaitForCrashRecoveryUnderLock();
            if (_crashNetworkRecoveryFailure is not null)
            {
                throw new InvalidOperationException(
                    "The previous mihomo crash did not restore owned Windows proxy state.",
                    _crashNetworkRecoveryFailure);
            }

            if (_processOwnershipFault is not null)
            {
                throw new InvalidOperationException(
                    "A prior App-owned mihomo generation did not release ownership safely.",
                    _processOwnershipFault);
            }

            if (_process is not null)
            {
                if (!_process.HasExited)
                {
                    return;
                }

                throw new InvalidOperationException(
                    "The previous App-owned mihomo generation is still completing crash recovery.");
            }

            ConcurrentBoundedTextBuffer startupOutput = new(StartupDiagnosticCapacity);
            IWindowsProcessJob processJob = _processJobFactory();
            WindowsJobProcess ownedProcess;
            try
            {
                ownedProcess = _processLauncher.Start(
                    processJob,
                    new WindowsJobProcessStartInfo(
                        BinaryPath,
                        configurationState.DirectoryPath,
                        ["-d", configurationState.DirectoryPath, "-f", configurationState.ConfigPath],
                        CaptureOutput: true));
            }
            catch (Exception launchFailure)
            {
                if (launchFailure is WindowsJobProcessCleanupException { AssignedToJob: false })
                {
                    _processJob = processJob;
                    _processOwnershipFault = launchFailure;
                    _processOwnershipFaultIsRecoverable = false;
                    throw;
                }

                try
                {
                    TerminateJobAndWaitForEmpty(processJob);
                    processJob.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw RetainOwnershipFailure(
                        processJob,
                        null,
                        null,
                        null,
                        launchFailure,
                        cleanupFailure);
                }

                ExceptionDispatchInfo.Capture(launchFailure).Throw();
                throw;
            }

            Process process = ownedProcess.Process;
            SynchronousProcessOutputDrain? outputDrain = null;
            InvalidOperationException? observedStartupFailure = null;
            try
            {
                outputDrain = new SynchronousProcessOutputDrain(
                    ownedProcess.StandardOutput,
                    ownedProcess.StandardError,
                    startupOutput);
                process.EnableRaisingEvents = false;
                if (process.WaitForExit(_startupObservationMilliseconds))
                {
                    process.WaitForExit();
                    TerminateGenerationAndWait(processJob, process, outputDrain);
                    outputDrain.ThrowIfFailed();
                    startupOutput.Complete();
                    string detail = startupOutput.Snapshot().Trim();
                    observedStartupFailure = new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                        ? "The bundled mihomo core exited during startup."
                        : $"The bundled mihomo core exited during startup: {detail}");
                }
                else
                {
                    startupOutput.Complete();
                    _processJob = processJob;
                    _ownedProcess = ownedProcess;
                    _outputDrain = outputDrain;
                    _process = process;
                    _processEpoch = Guid.NewGuid();
                    process.Exited += OnOwnedProcessExited;
                    process.EnableRaisingEvents = true;
                    return;
                }
            }
            catch (Exception startupFailure)
            {
                process.Exited -= OnOwnedProcessExited;
                try
                {
                    TerminateGenerationAndWait(processJob, process, outputDrain);
                }
                catch (Exception cleanupFailure)
                {
                    throw RetainOwnershipFailure(
                        processJob,
                        process,
                        ownedProcess,
                        outputDrain,
                        startupFailure,
                        cleanupFailure);
                }

                ClearGenerationFields(processJob, process, ownedProcess, outputDrain);
                DisposeGeneration(processJob, ownedProcess);
                ExceptionDispatchInfo.Capture(startupFailure).Throw();
                throw;
            }

            DisposeGeneration(processJob, ownedProcess);
            throw observedStartupFailure
                ?? new InvalidOperationException("The bundled mihomo core failed during startup observation.");
        }
    }

    /// <summary>Restarts the bundled mihomo core with <paramref name="configurationState"/>.</summary>
    /// <param name="configurationState">Managed configuration state whose file must exist.</param>
    /// <exception cref="FileNotFoundException">The bundled core binary or configuration file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The core process cannot be started.</exception>
    public void Restart(CoreConfigurationState configurationState)
    {
        Stop();
        Start(configurationState);
    }

    /// <summary>Stops the owned mihomo core process when it is running.</summary>
    public void Stop()
    {
        lock (_syncLock)
        {
            WaitForCrashRecoveryUnderLock();
            if (_processOwnershipFault is not null && !_processOwnershipFaultIsRecoverable)
            {
                throw new InvalidOperationException(
                    "A failed pre-Job mihomo launch could not be proven terminated; restart Clash# before transferring ownership.",
                    _processOwnershipFault);
            }

            if (_processJob is null)
            {
                if (_process is not null || _ownedProcess is not null || _outputDrain is not null)
                {
                    throw new InvalidOperationException("Mihomo process ownership is inconsistent: the generation Job is missing.");
                }

                return;
            }

            IWindowsProcessJob jobToStop = _processJob;
            Process? processToStop = _process;
            WindowsJobProcess? ownedProcessToDispose = _ownedProcess;
            SynchronousProcessOutputDrain? outputDrainToStop = _outputDrain;
            if (processToStop is not null)
            {
                processToStop.Exited -= OnOwnedProcessExited;
            }

            try
            {
                if (processToStop is null)
                {
                    TerminateJobAndWaitForEmpty(jobToStop);
                }
                else
                {
                    TerminateGenerationAndWait(jobToStop, processToStop, outputDrainToStop);
                }
            }
            catch (Exception cleanupFailure)
            {
                _processOwnershipFault = new InvalidOperationException(
                    "The App-owned mihomo Job did not become empty; ownership remains with the App.",
                    cleanupFailure);
                _processOwnershipFaultIsRecoverable = true;
                throw _processOwnershipFault;
            }

            ClearGenerationFields(jobToStop, processToStop, ownedProcessToDispose, outputDrainToStop);
            DisposeGeneration(jobToStop, ownedProcessToDispose);
        }
    }

    /// <summary>Terminates the complete owned generation and proves both Job and root process are empty/exited.</summary>
    private static void TerminateGenerationAndWait(
        IWindowsProcessJob job,
        Process process,
        SynchronousProcessOutputDrain? outputDrain)
    {
        TerminateJobAndWaitForEmpty(job);
        try
        {
            if (!process.WaitForExit(checked((int)ProcessExitTimeout.TotalMilliseconds)))
            {
                throw new InvalidOperationException(
                    "The App-owned mihomo core did not exit before the ownership handoff timeout.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The App-owned mihomo core could not release runtime ownership.",
                exception);
        }

        outputDrain?.WaitForCompletion(ProcessExitTimeout);
    }

    private static void TerminateJobAndWaitForEmpty(IWindowsProcessJob job)
    {
        job.TerminateAndWaitForEmpty(ProcessExitTimeout);
    }

    private AggregateException RetainOwnershipFailure(
        IWindowsProcessJob processJob,
        Process? process,
        WindowsJobProcess? ownedProcess,
        SynchronousProcessOutputDrain? outputDrain,
        Exception operationFailure,
        Exception cleanupFailure)
    {
        AggregateException ownershipFailure = new(
            "The App-owned mihomo generation failed and its Job could not be confirmed empty.",
            operationFailure,
            cleanupFailure);
        _processJob = processJob;
        _process = process;
        _ownedProcess = ownedProcess;
        _outputDrain = outputDrain;
        _processOwnershipFault = ownershipFailure;
        _processOwnershipFaultIsRecoverable = true;
        return ownershipFailure;
    }

    private void ClearGenerationFields(
        IWindowsProcessJob processJob,
        Process? process,
        WindowsJobProcess? ownedProcess,
        SynchronousProcessOutputDrain? outputDrain)
    {
        if (ReferenceEquals(_processJob, processJob))
        {
            _processJob = null;
        }

        if (process is null || ReferenceEquals(_process, process))
        {
            _process = null;
            _processEpoch = Guid.Empty;
        }

        if (ownedProcess is null || ReferenceEquals(_ownedProcess, ownedProcess))
        {
            _ownedProcess = null;
        }

        if (outputDrain is null || ReferenceEquals(_outputDrain, outputDrain))
        {
            _outputDrain = null;
        }

        _processOwnershipFault = null;
        _processOwnershipFaultIsRecoverable = true;
    }

    private bool TryGetCurrentProcessIdentityUnderLock(
        out MihomoAppProcessIdentity identity)
    {
        identity = default;
        if (_processEpoch == Guid.Empty
            || _process is null
            || _processOwnershipFault is not null
            || _crashNetworkRecoveryFailure is not null
            || _crashRecoveryInProgress)
        {
            return false;
        }

        try
        {
            if (_process.HasExited || _process.Id <= 0)
            {
                return false;
            }

            identity = new MihomoAppProcessIdentity(_processEpoch, _process.Id);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static void DisposeGeneration(
        IWindowsProcessJob processJob,
        WindowsJobProcess? ownedProcess)
    {
        processJob.Dispose();
        ownedProcess?.Dispose();
    }

    /// <summary>Publishes a natural long-running child exit after atomically releasing ownership.</summary>
    private void OnOwnedProcessExited(object? sender, EventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is not Process exitedProcess)
        {
            return;
        }

        EventHandler<MihomoCoreUnexpectedExitEventArgs>? handler;
        WindowsJobProcess? ownedProcess;
        SynchronousProcessOutputDrain? outputDrain;
        IWindowsProcessJob? processJob;
        lock (_syncLock)
        {
            // Explicit Stop holds this lock until it has cleared the field, so its
            // Exited callback is never classified as an unexpected runtime crash.
            if (!ReferenceEquals(_process, exitedProcess))
            {
                return;
            }

            ownedProcess = _ownedProcess;
            outputDrain = _outputDrain;
            processJob = _processJob;
            _crashRecoveryInProgress = true;
            handler = UnexpectedExit;
        }

        int? exitCode = null;
        try
        {
            exitCode = exitedProcess.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        Exception? ownershipCleanupFailure = null;
        if (processJob is null)
        {
            ownershipCleanupFailure = new InvalidOperationException(
                "The exited App-owned mihomo generation no longer has its Job ownership handle.");
        }
        else
        {
            try
            {
                TerminateGenerationAndWait(processJob, exitedProcess, outputDrain);
            }
            catch (Exception exception)
            {
                ownershipCleanupFailure = exception;
            }
        }

        lock (_syncLock)
        {
            if (ownershipCleanupFailure is null && processJob is not null)
            {
                ClearGenerationFields(processJob, exitedProcess, ownedProcess, outputDrain);
            }
            else
            {
                _processOwnershipFault = new InvalidOperationException(
                    "The crashed App-owned mihomo Job did not become empty; ownership remains with the App.",
                    ownershipCleanupFailure);
                _processOwnershipFaultIsRecoverable = true;
            }
        }

        if (ownershipCleanupFailure is null && processJob is not null)
        {
            try
            {
                DisposeGeneration(processJob, ownedProcess);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Ownership was already proven released. Managed wrapper disposal must not
                // suppress the crash-network recovery notification or strand its waiters.
            }
        }

        MihomoCoreUnexpectedExitEventArgs unexpectedExit = new(exitCode);
        try
        {
            handler?.Invoke(this, unexpectedExit);
        }
        finally
        {
            lock (_syncLock)
            {
                _crashNetworkRecoveryFailure = unexpectedExit.RecoveryFailure;
                _crashRecoveryInProgress = false;
                Monitor.PulseAll(_syncLock);
            }
        }
    }

    /// <summary>Clears a retained crash fault after WinINet recovery has succeeded synchronously.</summary>
    internal void AcknowledgeCrashNetworkRecovery()
    {
        lock (_syncLock)
        {
            WaitForCrashRecoveryUnderLock();
            _crashNetworkRecoveryFailure = null;
        }
    }

    private void WaitForCrashRecoveryUnderLock()
    {
        while (_crashRecoveryInProgress)
        {
            Monitor.Wait(_syncLock);
        }
    }

}

/// <summary>Adapts the App-owned Job root to the controller transport identity boundary.</summary>
internal sealed class MihomoCoreAppProcessIdentitySource(MihomoCoreService core)
    : IMihomoAppProcessIdentitySource
{
    private readonly MihomoCoreService _core = core
        ?? throw new ArgumentNullException(nameof(core));

    public MihomoAppProcessIdentity? CaptureCurrent() =>
        _core.CaptureAppProcessIdentity();

    public bool IsStillCurrent(MihomoAppProcessIdentity identity) =>
        _core.IsCurrentAppProcessIdentity(identity);
}

/// <summary>Describes an App-owned core process that exited outside an explicit stop.</summary>
internal sealed class MihomoCoreUnexpectedExitEventArgs(int? exitCode) : EventArgs
{
    public int? ExitCode { get; } = exitCode;

    /// <summary>Set by the recovery subscriber when owned network state could not be restored.</summary>
    public Exception? RecoveryFailure { get; set; }
}
