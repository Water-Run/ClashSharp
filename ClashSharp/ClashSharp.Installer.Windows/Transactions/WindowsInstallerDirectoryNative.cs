using System.Security.AccessControl;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Transactions;

internal sealed class WindowsInstallerDirectoryNative : IWindowsInstallerDirectoryNative
{
    public void CreateDirectory(string path, DirectorySecurity security)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(security);
        // Enable ownership recording together with terminal recovery and finalization; recording
        // alone leaves durable state that the production uninstall path cannot yet consume.
        new DirectoryInfo(path).Create(security);
    }

    /// <inheritdoc />
    public IWindowsInstallerDirectoryLease OpenDirectory(string path) =>
        new Lease(WindowsDirectoryReadLease.Open(path));

    private sealed class Lease(WindowsDirectoryReadLease inner) : IWindowsInstallerDirectoryLease
    {
        public WindowsDirectoryObservation Observe() => inner.Observe();

        public void Dispose() => inner.Dispose();
    }
}
