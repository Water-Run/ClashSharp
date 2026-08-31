using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal enum WindowsMachineAssociationFileStatus
{
    Missing,
    OrdinaryFile,
    Unsafe,
}

internal sealed record WindowsMachineAssociationFileObservation(
    WindowsMachineAssociationFileStatus Status,
    byte[]? Bytes)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(Status)
            || Status == WindowsMachineAssociationFileStatus.OrdinaryFile
                && (Bytes is null
                    || Bytes.Length is 0
                        or > InstallerMachineAssociationCodec.MaximumAssociationBytes)
            || Status != WindowsMachineAssociationFileStatus.OrdinaryFile
                && Bytes is not null)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_file_observation_invalid");
        }
    }
}

internal interface IWindowsMachineAssociationFileNative
{
    WindowsMachineAssociationFileObservation Read(string path);

    void WriteAtomically(string path, ReadOnlySpan<byte> bytes);

    void Delete(string path);
}

internal interface IWindowsMachineAssociationStore : IDisposable
{
    Task<InstallerMachineAssociationObservation> InspectAsync(
        CancellationToken cancellationToken);

    Task WriteAndVerifyAsync(
        InstallerMachineAssociation association,
        CancellationToken cancellationToken);

    Task DeleteAndVerifyAsync(CancellationToken cancellationToken);

    Task VerifyExactAsync(CancellationToken cancellationToken);

    Task VerifyAbsentAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Persists the strict owner/token association beneath the pinned protected service-data root and
/// reconciles every potentially acknowledged-late replace or delete by reading the final bytes.
/// </summary>
internal sealed class WindowsMachineAssociationStore : IWindowsMachineAssociationStore
{
    private readonly WindowsMachineDeploymentPlan _plan;
    private readonly IWindowsMachineRootGuard _rootGuard;
    private readonly IWindowsMachineAssociationFileNative _native;
    private readonly bool _ownsRootGuard;
    private bool _disposed;

    internal WindowsMachineAssociationStore(
        WindowsMachineDeploymentPlan plan,
        IWindowsMachineRootGuard rootGuard,
        IWindowsMachineAssociationFileNative native,
        bool ownsRootGuard = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rootGuard);
        ArgumentNullException.ThrowIfNull(native);
        plan.Validate();
        _plan = plan;
        _rootGuard = rootGuard;
        _native = native;
        _ownsRootGuard = ownsRootGuard;
    }

    internal static WindowsMachineAssociationStore CreateDefault(
        WindowsMachineDeploymentPlan plan)
    {
        WindowsMachineRootGuard guard = WindowsMachineRootGuard.CreateDefault(plan);
        return new WindowsMachineAssociationStore(
            plan,
            guard,
            WindowsMachineAssociationFileNative.Instance,
            ownsRootGuard: true);
    }

    public async Task<InstallerMachineAssociationObservation> InspectAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _rootGuard.EnsureProtectedAsync(_plan, cancellationToken)
            .ConfigureAwait(false);
        return InspectAfterRootValidation(cancellationToken);
    }

    public async Task WriteAndVerifyAsync(
        InstallerMachineAssociation association,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequirePlanAssociation(association);
        cancellationToken.ThrowIfCancellationRequested();
        await _rootGuard.EnsureProtectedAsync(_plan, cancellationToken)
            .ConfigureAwait(false);
        InstallerMachineAssociationObservation before =
            InspectAfterRootValidation(cancellationToken);
        if (before.Association == association)
        {
            return;
        }

        if (!_plan.Request.AllowReassociation
            && before.Status != InstallerMachineAssociationStatus.Missing)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_conflict");
        }

        byte[] bytes = InstallerMachineAssociationCodec.Serialize(association);
        Exception? mutationFailure = null;
        try
        {
            try
            {
                _native.WriteAtomically(_plan.AssociationPath, bytes);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                mutationFailure = exception;
            }

            InstallerMachineAssociationObservation after =
                InspectAfterRootValidation(cancellationToken);
            if (after.Association != association)
            {
                _ = mutationFailure;
                throw new InstallerStateUncertainException(
                    "installer.machine.association_state_uncertain");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task DeleteAndVerifyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _rootGuard.EnsureProtectedAsync(_plan, cancellationToken)
            .ConfigureAwait(false);
        InstallerMachineAssociationObservation before =
            InspectAfterRootValidation(cancellationToken);
        if (before.Status == InstallerMachineAssociationStatus.Missing)
        {
            return;
        }

        if (before.Association != _plan.Association)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_conflict");
        }

        Exception? mutationFailure = null;
        try
        {
            _native.Delete(_plan.AssociationPath);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            mutationFailure = exception;
        }

        InstallerMachineAssociationObservation after =
            InspectAfterRootValidation(cancellationToken);
        if (after.Status != InstallerMachineAssociationStatus.Missing)
        {
            _ = mutationFailure;
            throw new InstallerStateUncertainException(
                "installer.machine.association_state_uncertain");
        }
    }

    public async Task VerifyExactAsync(CancellationToken cancellationToken)
    {
        InstallerMachineAssociationObservation observation =
            await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (observation.Association != _plan.Association)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_verification_failed");
        }
    }

    public async Task VerifyAbsentAsync(CancellationToken cancellationToken)
    {
        InstallerMachineAssociationObservation observation =
            await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (observation.Status != InstallerMachineAssociationStatus.Missing)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_removal_verification_failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsRootGuard)
        {
            _rootGuard.Dispose();
        }

        _disposed = true;
    }

    private InstallerMachineAssociationObservation InspectAfterRootValidation(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsMachineAssociationFileObservation file;
        try
        {
            file = _native.Read(_plan.AssociationPath);
            file.Validate();
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.machine.association_read_failed",
                exception);
        }

        if (file.Status == WindowsMachineAssociationFileStatus.Missing)
        {
            return InstallerMachineAssociationObservation.Missing();
        }

        if (file.Status == WindowsMachineAssociationFileStatus.Unsafe)
        {
            return InstallerMachineAssociationObservation.Invalid();
        }

        byte[] bytes = file.Bytes!;
        try
        {
            try
            {
                return InstallerMachineAssociationObservation.Valid(
                    InstallerMachineAssociationCodec.Parse(bytes));
            }
            catch (InstallerProtocolException)
            {
                return InstallerMachineAssociationObservation.Invalid();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void RequirePlanAssociation(InstallerMachineAssociation association)
    {
        ArgumentNullException.ThrowIfNull(association);
        association.Validate();
        if (association != _plan.Association)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_plan_changed");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or OperationCanceledException
            or InstallerProtocolException
            or InstallerStateUncertainException);
}

internal sealed class WindowsMachineAssociationFileNative
    : IWindowsMachineAssociationFileNative
{
    private const uint MoveFileReplaceExisting = 0x0000_0001;
    private const uint MoveFileWriteThrough = 0x0000_0008;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    internal static WindowsMachineAssociationFileNative Instance { get; } = new();

    private WindowsMachineAssociationFileNative()
    {
    }

    public WindowsMachineAssociationFileObservation Read(string path)
    {
        ValidatePath(path);
        SafeFileHandle? handle = null;
        try
        {
            handle = WindowsFileSystemNative.OpenOrdinaryFile(path);
            long length = RandomAccess.GetLength(handle);
            if (length is <= 0
                or > InstallerMachineAssociationCodec.MaximumAssociationBytes)
            {
                return new(WindowsMachineAssociationFileStatus.Unsafe, null);
            }

            byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
                if (read == 0)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return new(WindowsMachineAssociationFileStatus.Unsafe, null);
                }

                offset = checked(offset + read);
            }

            return new(WindowsMachineAssociationFileStatus.OrdinaryFile, bytes);
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return new(WindowsMachineAssociationFileStatus.Missing, null);
        }
        catch (FileNotFoundException)
        {
            return new(WindowsMachineAssociationFileStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(WindowsMachineAssociationFileStatus.Missing, null);
        }
        catch (IOException)
        {
            return new(WindowsMachineAssociationFileStatus.Unsafe, null);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        ValidatePath(path);
        if (bytes.IsEmpty
            || bytes.Length > InstallerMachineAssociationCodec.MaximumAssociationBytes)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_size_invalid");
        }

        string directory = Path.GetDirectoryName(path)
            ?? throw new InstallerProtocolException(
                "installer.machine.association_path_invalid");
        string temporary = string.Concat(path, ".new");
        using SafeFileHandle directoryGuard =
            WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(directory);
        DeleteTemporaryIfPresent(temporary);
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            RequireOrdinaryIfPresent(path);
            if (!MoveFileEx(
                    temporary,
                    path,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            DeleteTemporaryIfPresent(temporary);
        }
    }

    public void Delete(string path)
    {
        ValidatePath(path);
        string directory = Path.GetDirectoryName(path)
            ?? throw new InstallerProtocolException(
                "installer.machine.association_path_invalid");
        using SafeFileHandle directoryGuard =
            WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(directory);
        if (!RequireOrdinaryIfPresent(path))
        {
            return;
        }

        File.Delete(path);
    }

    private static bool RequireOrdinaryIfPresent(string path)
    {
        try
        {
            using SafeFileHandle file = WindowsFileSystemNative.OpenOrdinaryFile(path);
            return true;
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void DeleteTemporaryIfPresent(string path)
    {
        if (RequireOrdinaryIfPresent(path))
        {
            File.Delete(path);
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(path),
                "association.json",
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.association_path_invalid");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}
