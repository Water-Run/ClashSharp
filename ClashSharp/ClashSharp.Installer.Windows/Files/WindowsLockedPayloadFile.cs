using System.Buffers;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Files;

internal sealed class WindowsLockedPayloadFile : IInstallerLockedPayloadFile, IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly WindowsFileIdentity _identity;
    private bool _disposed;

    private WindowsLockedPayloadFile(
        string fullPath,
        InstallerPayloadFileEntry manifestEntry,
        SafeFileHandle handle,
        WindowsFileIdentity identity)
    {
        FullPath = fullPath;
        ManifestEntry = manifestEntry;
        _handle = handle;
        _identity = identity;
    }

    public InstallerPayloadFileEntry ManifestEntry { get; }

    public string FullPath { get; }

    internal static WindowsLockedPayloadFile Open(
        string fullPath,
        InstallerPayloadFileEntry manifestEntry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryFile(fullPath);
        try
        {
            var locked = new WindowsLockedPayloadFile(
                fullPath,
                manifestEntry,
                handle,
                WindowsFileSystemNative.GetOrdinaryFileIdentity(handle));
            locked.VerifyOpenObject(cancellationToken);
            return locked;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void Reverify(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle probe = WindowsFileSystemNative.OpenOrdinaryFile(FullPath);
        if (WindowsFileSystemNative.GetOrdinaryFileIdentity(probe) != _identity
            || RandomAccess.GetLength(probe) != ManifestEntry.Length)
        {
            throw new InstallerProtocolException("installer.release.locked_file_changed");
        }

        VerifyOpenObject(cancellationToken);
    }

    internal byte[] ReadAllBytes(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (ManifestEntry.Length > int.MaxValue)
        {
            throw new InstallerProtocolException("installer.release.payload_file_size_invalid");
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)ManifestEntry.Length));
        long offset = 0;
        while (offset < bytes.LongLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(_handle, bytes.AsSpan(checked((int)offset)), offset);
            if (read == 0)
            {
                throw new InstallerProtocolException("installer.release.locked_file_changed");
            }

            offset = checked(offset + read);
        }

        return bytes;
    }

    internal FileStream OpenVerifiedReadStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryFile(FullPath);
        try
        {
            if (WindowsFileSystemNative.GetOrdinaryFileIdentity(handle) != _identity
                || RandomAccess.GetLength(handle) != ManifestEntry.Length)
            {
                throw new InstallerProtocolException("installer.release.locked_file_changed");
            }

            return new FileStream(handle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _handle.Dispose();
        _disposed = true;
    }

    private void VerifyOpenObject(CancellationToken cancellationToken)
    {
        if (WindowsFileSystemNative.GetOrdinaryFileIdentity(_handle) != _identity
            || RandomAccess.GetLength(_handle) != ManifestEntry.Length)
        {
            throw new InstallerProtocolException("installer.release.locked_file_changed");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < ManifestEntry.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, ManifestEntry.Length - offset);
                int read = RandomAccess.Read(_handle, buffer.AsSpan(0, requested), offset);
                if (read == 0)
                {
                    throw new InstallerProtocolException("installer.release.locked_file_changed");
                }

                hash.AppendData(buffer, 0, read);
                offset = checked(offset + read);
            }

            string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualHash, ManifestEntry.Sha256, StringComparison.Ordinal))
            {
                throw new InstallerProtocolException("installer.release.locked_file_hash_mismatch");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
