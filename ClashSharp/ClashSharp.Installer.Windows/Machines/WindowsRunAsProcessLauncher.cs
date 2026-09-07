using System.ComponentModel;
using System.Diagnostics;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsRunAsProcessLauncher
{
    Task<IWindowsElevatedHelperProcess> StartAsync(
        string executablePath,
        InstallerMachineHelperBootstrap bootstrap,
        CancellationToken cancellationToken);
}

internal interface IWindowsElevatedHelperProcess : IDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Starts the broker-preverified single-file Installer's helper branch through runas on an STA thread.
/// This launcher validates bootstrap shape, not file identity or Authenticode trust.
/// </summary>
internal sealed class WindowsRunAsProcessLauncher : IWindowsRunAsProcessLauncher, IWindowsOwnerTransferProcessLauncher
{
    private const int ErrorCancelled = 1223;
    private readonly Func<ProcessStartInfo, Process?> _start;

    internal WindowsRunAsProcessLauncher()
        : this(static startInfo => Process.Start(startInfo))
    {
    }

    internal WindowsRunAsProcessLauncher(Func<ProcessStartInfo, Process?> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _start = start;
    }

    public Task<IWindowsElevatedHelperProcess> StartAsync(
        string executablePath,
        InstallerMachineHelperBootstrap bootstrap,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(bootstrap);
        cancellationToken.ThrowIfCancellationRequested();
        bootstrap.Validate();
        return StartAsync(executablePath, bootstrap.ToArguments(), cancellationToken);
    }

    public Task<IWindowsElevatedHelperProcess> StartAsync(
        string executablePath, InstallerOwnerTransferBootstrap bootstrap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        return StartAsync(executablePath, bootstrap.ToArguments(), cancellationToken);
    }

    private Task<IWindowsElevatedHelperProcess> StartAsync(
        string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new InstallerProtocolException(
                "installer.elevation.executable_path_invalid");
        }

        string fullPath = Path.GetFullPath(executablePath);
        if (!IsCanonicalDriveQualifiedPath(fullPath)
            || !string.Equals(
                Path.GetFileName(fullPath),
                InstallerArtifactNames.PublishedExecutable,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.elevation.executable_path_invalid");
        }

        ProcessStartInfo startInfo = CreateStartInfo(fullPath, arguments);
        var completion = new TaskCompletionSource<IWindowsElevatedHelperProcess>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => StartOnSta(startInfo, completion, cancellationToken))
        {
            IsBackground = true,
            Name = "ClashSharp Installer elevation launcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool IsCanonicalDriveQualifiedPath(string fullPath) =>
        fullPath.Length >= 3
        && char.IsAsciiLetter(fullPath[0])
        && fullPath[1] == ':'
        && fullPath[2] == Path.DirectorySeparatorChar;

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false,
            ErrorDialog = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private void StartOnSta(
        ProcessStartInfo startInfo,
        TaskCompletionSource<IWindowsElevatedHelperProcess> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process process = _start(startInfo)
                ?? throw new InstallerProtocolException(
                    "installer.elevation.process_missing");
            completion.TrySetResult(new WindowsElevatedHelperProcess(process));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            completion.TrySetException(new InstallerUserCancelledException(
                "installer.elevation.user_cancelled"));
        }
        catch (InstallerProtocolException exception)
        {
            completion.TrySetException(exception);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            completion.TrySetException(new InstallerProtocolException(
                "installer.elevation.launch_failed",
                exception));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

internal sealed class WindowsElevatedHelperProcess : IWindowsElevatedHelperProcess
{
    private readonly Process _process;

    internal WindowsElevatedHelperProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    internal Process ProcessForTesting => _process;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    public void Dispose() => _process.Dispose();
}
