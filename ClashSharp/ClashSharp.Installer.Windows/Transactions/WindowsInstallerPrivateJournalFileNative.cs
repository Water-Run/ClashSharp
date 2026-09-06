using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Transactions;

internal interface IWindowsInstallerPrivateJournalFileNative
{
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken);

    Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

internal interface IWindowsInstallerPrivateJournalPresenceNative
{
    bool IsPresent(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Accesses only the fixed private journal leaf under a caller-pinned directory chain. Every file
/// handle is checked for ordinary kind, a single hard link and an exact private security descriptor.
/// </summary>
internal sealed class WindowsInstallerPrivateJournalFileNative :
    IWindowsInstallerPrivateJournalFileNative,
    IWindowsInstallerPrivateJournalPresenceNative
{
    private const uint MoveFileReplaceExisting = 1;
    private const uint MoveFileWriteThrough = 8;

    internal static WindowsInstallerPrivateJournalFileNative Instance { get; } = new();

    private WindowsInstallerPrivateJournalFileNative()
    {
    }

    public bool IsPresent(string path, CancellationToken cancellationToken)
    {
        ValidateJournalPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle? file = OpenPrivateIfPresent(path);
        cancellationToken.ThrowIfCancellationRequested();
        return file is not null;
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ValidateJournalPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle? file = OpenPrivateIfPresent(path);
        if (file is null)
        {
            return null;
        }

        long length = RandomAccess.GetLength(file);
        if (length is < 1 or > InstallerOwnerTransferCodec.MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.owner_transfer.document_size_invalid");
        }

        byte[] bytes = new byte[checked((int)length)];
        bool returned = false;
        try
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = await RandomAccess.ReadAsync(file, bytes.AsMemory(offset), offset, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new InstallerProtocolException("installer.owner_transfer.document_changed");
                }

                offset += count;
            }

            if (RandomAccess.GetLength(file) != length)
            {
                throw new InstallerProtocolException("installer.owner_transfer.document_changed");
            }

            cancellationToken.ThrowIfCancellationRequested();
            returned = true;
            return bytes;
        }
        finally
        {
            if (!returned)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    public async Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        ValidateJournalPath(path);
        if (bytes.IsEmpty || bytes.Length > InstallerOwnerTransferCodec.MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.owner_transfer.document_size_invalid");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (OpenPrivateIfPresent(path))
        {
            // Reject an unsafe existing destination before creating any private temporary bytes.
        }

        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{InstallerOwnerTransferStateLayout.JournalFileName}.{Guid.NewGuid():N}.tmp");
        bool temporaryCreated = false;
        try
        {
            await using (FileStream stream = new FileInfo(temporary).Create(
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough,
                WindowsInstallerPrivateStateSecurity.CreateFileSecurity()))
            {
                temporaryCreated = true;
                ValidatePrivateFile(stream.SafeFileHandle);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (OpenPrivateIfPresent(path))
            {
                // The machine authority excludes other installer writers; the pinned private root
                // excludes user replacement between this final handle check and same-directory rename.
            }

            if (!MoveFileEx(temporary, path, MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new InstallerStateUncertainException("installer.owner_transfer.atomic_replace_uncertain");
            }

            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated)
            {
                TryDeleteOwnedTemporary(temporary);
            }
        }
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        ValidateJournalPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        using (SafeFileHandle? file = OpenPrivateIfPresent(path))
        {
            if (file is null)
            {
                return Task.CompletedTask;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return Task.CompletedTask;
    }

    private static SafeFileHandle? OpenPrivateIfPresent(string path)
    {
        SafeFileHandle? file = null;
        try
        {
            file = WindowsFileSystemNative.OpenOrdinaryFile(path);
            ValidatePrivateFile(file);
            return file;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2 && file is null)
        {
            file?.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            file?.Dispose();
            throw new InstallerProtocolException("installer.owner_transfer.file_open_failed");
        }
        catch
        {
            file?.Dispose();
            throw;
        }
    }

    private static void ValidatePrivateFile(SafeFileHandle file)
    {
        _ = WindowsFileSystemNative.GetOrdinaryFileIdentity(file);
        if (WindowsFileSystemNative.GetLinkCount(file) != 1)
        {
            throw new InstallerProtocolException("installer.owner_transfer.file_links_invalid");
        }

        WindowsInstallerPrivateStateSecurity.Validate(
            WindowsInstallerDirectoryLease.ReadSecuritySnapshot(file), directory: false);
    }

    private static void ValidateJournalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(path), InstallerOwnerTransferStateLayout.JournalFileName, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.owner_transfer.journal_path_invalid");
        }
    }

    private static void TryDeleteOwnedTemporary(string path)
    {
        try
        {
            using (OpenPrivateIfPresent(path))
            {
            }

            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InstallerProtocolException)
        {
            // Only this invocation's random temporary path is eligible for cleanup. An abandoned
            // private temporary is never trusted as journal state or removed by a subsequent writer.
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
