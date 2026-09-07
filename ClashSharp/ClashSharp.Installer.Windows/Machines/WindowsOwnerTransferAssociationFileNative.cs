using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Windows.FileSecurity;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Replaces only the exact recorded association under caller-pinned, new-owner roots. A fixed
/// transaction-bound temporary survives process termination: only its exact ACL and a prefix of
/// this transaction's new canonical bytes authorize removal/recreation. Unknown evidence is kept.
/// The caller holds global authority until post-error observation has drained.
/// </summary>
internal sealed class WindowsOwnerTransferAssociationFileNative : IWindowsOwnerTransferAssociationFileNative
{
    public Task<WindowsOwnerTransferAssociationObservation> InspectAsync(
        WindowsOwnerTransferAssociationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        InstallerMachineAssociation association = ReadAssociation(plan);
        byte[] desired = InstallerMachineAssociationCodec.Serialize(plan.Next);
        try
        {
            bool temporary = InspectTemporary(plan, desired);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WindowsOwnerTransferAssociationObservation(association, temporary));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(desired);
        }
    }

    public async Task ReplaceExactAsync(WindowsOwnerTransferAssociationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        InstallerMachineAssociation before = ReadAssociation(plan);
        RequireParticipant(plan, before);
        byte[] desired = InstallerMachineAssociationCodec.Serialize(plan.Next);
        bool created = false;
        try
        {
            // The nonce is already durable before this phase. Zero-length and partial prefix files
            // can be our interrupted create/write; foreign ACLs, links or content never qualify.
            RemoveVerifiedTemporary(plan, desired);
            cancellationToken.ThrowIfCancellationRequested();
            if (before == plan.Next)
            {
                return;
            }

            await using (var stream = new FileStream(plan.TemporaryPath, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                ValidateFile(stream.SafeFileHandle, plan.Next.OwnerSid);
                await stream.WriteAsync(desired, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            before = ReadAssociation(plan);
            RequireParticipant(plan, before);
            if (before == plan.Next)
            {
                return;
            }
            if (!InspectTemporary(plan, desired, requireComplete: true))
            {
                throw new InstallerProtocolException("installer.owner_transfer.association_temporary_missing");
            }
            cancellationToken.ThrowIfCancellationRequested();
            // All users have read-only access; global exclusion and pinned parents prevent other
            // installer writers or user replacement between these final checks and atomic rename.
            if (!MoveFileEx(plan.TemporaryPath, plan.AssociationPath, 1 | 8))
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
                    RemoveVerifiedTemporary(plan, desired);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(desired);
            }
        }
    }

    private static InstallerMachineAssociation ReadAssociation(WindowsOwnerTransferAssociationPlan plan)
    {
        using SafeFileHandle file = WindowsFileSystemNative.OpenOrdinaryFile(plan.AssociationPath);
        ValidateFile(file, plan.Next.OwnerSid);
        byte[] bytes = ReadBounded(file, allowEmpty: false);
        try
        {
            return InstallerMachineAssociationCodec.Parse(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void RequireParticipant(WindowsOwnerTransferAssociationPlan plan, InstallerMachineAssociation observed)
    {
        if (observed != plan.Previous && observed != plan.Next)
        {
            throw new InstallerProtocolException("installer.owner_transfer.association_conflict");
        }
    }

    private static bool InspectTemporary(
        WindowsOwnerTransferAssociationPlan plan, ReadOnlySpan<byte> desired, bool requireComplete = false)
    {
        SafeFileHandle file;
        try
        {
            file = WindowsFileSystemNative.OpenOrdinaryFile(plan.TemporaryPath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            return false;
        }
        using (file)
        {
            ValidateFile(file, plan.Next.OwnerSid);
            byte[] bytes = ReadBounded(file, allowEmpty: true);
            try
            {
                if (!desired.StartsWith(bytes) || (requireComplete && bytes.Length != desired.Length))
                {
                    throw new InstallerProtocolException("installer.owner_transfer.association_temporary_conflict");
                }
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static void RemoveVerifiedTemporary(WindowsOwnerTransferAssociationPlan plan, ReadOnlySpan<byte> desired)
    {
        if (InspectTemporary(plan, desired))
        {
            // This path is derived solely from fixed roots and the validated durable transaction ID.
            File.Delete(plan.TemporaryPath);
        }
    }

    private static byte[] ReadBounded(SafeFileHandle file, bool allowEmpty)
    {
        long length = RandomAccess.GetLength(file);
        if (length < (allowEmpty ? 0 : 1) || length > InstallerMachineAssociationCodec.MaximumAssociationBytes)
        {
            throw new InstallerProtocolException("installer.owner_transfer.association_size_invalid");
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
                    throw new InstallerProtocolException("installer.owner_transfer.association_file_changed");
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

    private static void ValidateFile(SafeFileHandle file, string nextSid)
    {
        _ = WindowsFileSystemNative.GetOrdinaryFileIdentity(file);
        if (WindowsFileSystemNative.GetLinkCount(file) != 1
            || !WindowsOwnerTransferAccessPolicy.HasOwnerAccess(
                WindowsDirectoryReadLease.ReadSecuritySnapshot(file), nextSid, directory: false, inherited: true))
        {
            throw new InstallerProtocolException("installer.owner_transfer.association_file_invalid");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
