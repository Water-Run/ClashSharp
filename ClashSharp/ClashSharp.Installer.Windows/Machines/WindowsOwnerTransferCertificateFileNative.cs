using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Transactions;
using ClashSharp.Windows.FileSecurity;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Moves exact ownership evidence between the old-readable active slot and two private account
/// archives. Caller-owned parent handles and global exclusion cover all checks, mutations and
/// reconciliation. Only this invocation's validated temporary is cleaned; unknown evidence stays.
/// </summary>
internal sealed class WindowsOwnerTransferCertificateFileNative : IWindowsOwnerTransferCertificateFileNative
{
    public Task<InstallerOwnerTransferCertificateState> ReadAsync(
        WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var state = new InstallerOwnerTransferCertificateState(
            ReadLedger(plan.ActivePath, plan, isPrivate: false),
            ReadLedger(plan.PreviousArchivePath, plan, isPrivate: true),
            ReadLedger(plan.NextArchivePath, plan, isPrivate: true));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state);
    }

    public async Task ApplyAsync(WindowsOwnerTransferCertificatePlan plan,
        InstallerOwnerTransferCertificateState expected, InstallerOwnerTransferCertificateStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(step);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (expected.GetNextStep(plan.Journal) != step
            || await ReadAsync(plan, cancellationToken).ConfigureAwait(false) != expected)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_write_conflict");
        }
        cancellationToken.ThrowIfCancellationRequested();
        switch (step.Action)
        {
            case InstallerOwnerTransferCertificateAction.PreservePrevious:
                await WriteAsync(plan.PreviousArchivePath, plan.Journal.PreviousCertificateLedger!,
                    plan, isPrivate: true, overwrite: false, cancellationToken).ConfigureAwait(false);
                break;
            case InstallerOwnerTransferCertificateAction.ActivateNext:
                if (plan.Journal.NextCertificateLedger is { } next)
                {
                    await WriteAsync(plan.ActivePath, next, plan, isPrivate: false,
                        overwrite: true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    File.Delete(plan.ActivePath);
                }
                break;
            case InstallerOwnerTransferCertificateAction.ReleaseActivatedArchive:
                File.Delete(plan.NextArchivePath);
                break;
            default:
                throw new InstallerProtocolException("installer.owner_transfer.certificate_step_invalid");
        }
    }

    private static InstallerCertificateOwnershipLedger? ReadLedger(
        string path, WindowsOwnerTransferCertificatePlan plan, bool isPrivate)
    {
        using SafeFileHandle? file = OpenIfPresent(path, plan, isPrivate);
        if (file is null)
        {
            return null;
        }
        byte[] bytes = ReadBounded(file, allowEmpty: false);
        byte[]? canonical = null;
        try
        {
            InstallerCertificateOwnershipLedger ledger = InstallerCertificateOwnershipCodec.Parse(bytes);
            canonical = InstallerCertificateOwnershipCodec.Serialize(ledger);
            if (!CryptographicOperations.FixedTimeEquals(bytes, canonical))
            {
                throw new InstallerProtocolException("installer.owner_transfer.certificate_document_invalid");
            }
            return ledger;
        }
        catch (InstallerProtocolException)
        {
            // The ordinary codec can retain parser details. No private account evidence leaves
            // this dedicated transfer boundary through exception text or an inner exception.
            throw new InstallerProtocolException("installer.owner_transfer.certificate_document_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (canonical is not null)
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
        }
    }

    private static async Task WriteAsync(string path, InstallerCertificateOwnershipLedger ledger,
        WindowsOwnerTransferCertificatePlan plan, bool isPrivate, bool overwrite, CancellationToken cancellationToken)
    {
        byte[] bytes = InstallerCertificateOwnershipCodec.Serialize(ledger);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".certificate-transfer-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            await using (FileStream stream = isPrivate
                ? new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough,
                    WindowsInstallerPrivateStateSecurity.CreateFileSecurity())
                : new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                ValidateFile(stream.SafeFileHandle, plan, isPrivate);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Private archives are only created; an existing archive cannot be overwritten.
            // The active slot changes only inside the already compared three-slot state.
            if (!MoveFileEx(temporary, path, (overwrite ? 1u : 0u) | 8u))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            created = false;
        }
        finally
        {
            try
            {
                if (created)
                {
                    RemoveOwnedTemporary(temporary, bytes, plan, isPrivate);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static void RemoveOwnedTemporary(string path, ReadOnlySpan<byte> desired,
        WindowsOwnerTransferCertificatePlan plan, bool isPrivate)
    {
        using (SafeFileHandle? file = OpenIfPresent(path, plan, isPrivate))
        {
            if (file is null)
            {
                return;
            }
            byte[] actual = ReadBounded(file, allowEmpty: true);
            try
            {
                if (!desired.StartsWith(actual))
                {
                    throw new InstallerProtocolException("installer.owner_transfer.certificate_temporary_conflict");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
        File.Delete(path);
    }

    private static SafeFileHandle? OpenIfPresent(string path, WindowsOwnerTransferCertificatePlan plan, bool isPrivate)
    {
        SafeFileHandle file;
        try
        {
            file = WindowsFileSystemNative.OpenOrdinaryFile(path);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            return null;
        }
        try
        {
            ValidateFile(file, plan, isPrivate);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static void ValidateFile(SafeFileHandle file, WindowsOwnerTransferCertificatePlan plan, bool isPrivate)
    {
        _ = WindowsFileSystemNative.GetOrdinaryFileIdentity(file);
        if (WindowsFileSystemNative.GetLinkCount(file) != 1)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_file_invalid");
        }
        WindowsDirectorySecuritySnapshot security = WindowsDirectoryReadLease.ReadSecuritySnapshot(file);
        if (isPrivate)
        {
            WindowsInstallerPrivateStateSecurity.Validate(security, directory: false);
        }
        else if (!WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
            security, plan.Journal.PreviousOwner.Association.OwnerSid, directory: false, inherited: true))
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_file_invalid");
        }
    }

    private static byte[] ReadBounded(SafeFileHandle file, bool allowEmpty)
    {
        long length = RandomAccess.GetLength(file);
        if (length < (allowEmpty ? 0 : 1) || length > InstallerCertificateOwnershipCodec.MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_size_invalid");
        }
        byte[] bytes = new byte[checked((int)length)];
        try
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = RandomAccess.Read(file, bytes.AsSpan(offset), offset);
                if (read == 0)
                {
                    throw new InstallerProtocolException("installer.owner_transfer.certificate_file_changed");
                }
                offset += read;
            }
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
