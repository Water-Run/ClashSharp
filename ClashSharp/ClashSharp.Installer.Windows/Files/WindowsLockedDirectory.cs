using ClashSharp.Installer.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Files;

internal sealed class WindowsLockedDirectory : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly WindowsFileIdentity _identity;
    private readonly bool _attributeOnly;
    private bool _disposed;

    private WindowsLockedDirectory(
        string fullPath,
        SafeFileHandle handle,
        WindowsFileIdentity identity,
        bool attributeOnly)
    {
        FullPath = fullPath;
        _handle = handle;
        _identity = identity;
        _attributeOnly = attributeOnly;
    }

    internal string FullPath { get; }

    internal static WindowsLockedDirectory Open(string fullPath) => OpenCore(fullPath, attributeOnly: false);

    internal static WindowsLockedDirectory OpenForObservation(string fullPath) => OpenCore(fullPath, attributeOnly: true);

    private static WindowsLockedDirectory OpenCore(string fullPath, bool attributeOnly)
    {
        SafeFileHandle handle = attributeOnly ? WindowsFileSystemNative.OpenOrdinaryDirectoryForObservation(fullPath)
            : WindowsFileSystemNative.OpenOrdinaryDirectory(fullPath);
        try
        {
            return new WindowsLockedDirectory(
                fullPath,
                handle,
                WindowsFileSystemNative.GetOrdinaryDirectoryIdentity(handle), attributeOnly);
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
        using SafeFileHandle probe = _attributeOnly ? WindowsFileSystemNative.OpenOrdinaryDirectoryForObservation(FullPath)
            : WindowsFileSystemNative.OpenOrdinaryDirectory(FullPath);
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
