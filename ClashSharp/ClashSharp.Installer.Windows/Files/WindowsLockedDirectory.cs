using ClashSharp.Installer.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Files;

internal sealed class WindowsLockedDirectory : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly WindowsFileIdentity _identity;
    private bool _disposed;

    private WindowsLockedDirectory(
        string fullPath,
        SafeFileHandle handle,
        WindowsFileIdentity identity)
    {
        FullPath = fullPath;
        _handle = handle;
        _identity = identity;
    }

    internal string FullPath { get; }

    internal static WindowsLockedDirectory Open(string fullPath)
    {
        SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryDirectory(fullPath);
        try
        {
            return new WindowsLockedDirectory(
                fullPath,
                handle,
                WindowsFileSystemNative.GetOrdinaryDirectoryIdentity(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void Reverify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using SafeFileHandle probe = WindowsFileSystemNative.OpenOrdinaryDirectory(FullPath);
        if (WindowsFileSystemNative.GetOrdinaryDirectoryIdentity(probe) != _identity
            || WindowsFileSystemNative.GetOrdinaryDirectoryIdentity(_handle) != _identity)
        {
            throw new InstallerProtocolException("installer.release.locked_directory_changed");
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
}
