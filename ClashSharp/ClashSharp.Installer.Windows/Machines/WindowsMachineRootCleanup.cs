using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal enum WindowsMachineRootState
{
    Missing,
    EmptyOrdinaryDirectory,
    NotEmpty,
    Unsafe,
}

internal interface IWindowsMachineRootCleanupNative
{
    WindowsMachineRootState Inspect(string path);

    void DeleteEmpty(string path);
}

/// <summary>Removes only the two exact, already-empty protected leaf roots after all resources.</summary>
internal sealed class WindowsMachineRootCleanup
{
    private readonly IWindowsMachineRootCleanupNative _native;

    internal WindowsMachineRootCleanup()
        : this(WindowsMachineRootCleanupNative.Instance)
    {
    }

    internal WindowsMachineRootCleanup(IWindowsMachineRootCleanupNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    internal void RemoveAndVerify(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string root in new[] { plan.MachineRoot, plan.ServiceDataRoot })
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsMachineRootState before = Inspect(root);
            if (before == WindowsMachineRootState.Missing)
            {
                continue;
            }

            if (before != WindowsMachineRootState.EmptyOrdinaryDirectory)
            {
                throw new InstallerProtocolException(
                    "installer.machine.root_cleanup_unsafe");
            }

            Exception? failure = null;
            try
            {
                _native.DeleteEmpty(root);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                failure = exception;
            }

            if (Inspect(root) != WindowsMachineRootState.Missing)
            {
                _ = failure;
                throw new InstallerStateUncertainException(
                    "installer.machine.root_cleanup_uncertain");
            }
        }
    }

    internal void VerifyAbsent(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        VerifyAbsent(plan.Roots, cancellationToken);
    }

    internal void VerifyAbsent(
        WindowsMachineDeploymentRoots roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        roots.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (Inspect(roots.MachineRoot) != WindowsMachineRootState.Missing
            || Inspect(roots.ServiceDataRoot) != WindowsMachineRootState.Missing)
        {
            throw new InstallerProtocolException(
                "installer.machine.root_removal_verification_failed");
        }
    }

    private WindowsMachineRootState Inspect(string path)
    {
        try
        {
            WindowsMachineRootState state = _native.Inspect(path);
            return Enum.IsDefined(state)
                ? state
                : throw new InstallerProtocolException(
                    "installer.machine.root_state_invalid");
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.machine.root_cleanup_inspection_failed",
                exception);
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or InstallerProtocolException
            or InstallerStateUncertainException
            or OperationCanceledException);
}

internal sealed class WindowsMachineRootCleanupNative : IWindowsMachineRootCleanupNative
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FileDispositionInfoClass = 4;

    internal static WindowsMachineRootCleanupNative Instance { get; } = new();

    private WindowsMachineRootCleanupNative()
    {
    }

    public WindowsMachineRootState Inspect(string path)
    {
        ValidatePath(path);
        try
        {
            using SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryDirectory(path);
            using IEnumerator<string> entries = Directory
                .EnumerateFileSystemEntries(path)
                .GetEnumerator();
            return entries.MoveNext()
                ? WindowsMachineRootState.NotEmpty
                : WindowsMachineRootState.EmptyOrdinaryDirectory;
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return WindowsMachineRootState.Missing;
        }
        catch (FileNotFoundException)
        {
            return WindowsMachineRootState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return WindowsMachineRootState.Missing;
        }
        catch (IOException)
        {
            return WindowsMachineRootState.Unsafe;
        }
    }

    public void DeleteEmpty(string path)
    {
        ValidatePath(path);
        SafeFileHandle handle;
        try
        {
            handle = WindowsFileSystemNative.OpenOrdinaryDirectoryForDeletion(path);
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return;
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        using (handle)
        {
            using IEnumerator<string> entries = Directory
                .EnumerateFileSystemEntries(path)
                .GetEnumerator();
            if (entries.MoveNext())
            {
                throw new InstallerProtocolException(
                    "installer.machine.root_cleanup_unsafe");
            }

            var disposition = new FileDispositionInformation
            {
                DeleteFile = 1,
            };
            if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInfoClass,
                    in disposition,
                    checked((uint)Marshal.SizeOf<FileDispositionInformation>())))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path) is not ("Service" or "MihomoService"))
        {
            throw new InstallerProtocolException(
                "installer.machine.root_cleanup_path_invalid");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        in FileDispositionInformation fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        internal byte DeleteFile;
    }
}
