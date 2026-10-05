namespace ClashSharp.Installer.Windows.Machines;

/// <summary>Observes all ordinary original-package bytes without retaining authority after return.</summary>
internal sealed class WindowsMaintenancePackageStateReader : IWindowsMaintenancePackageStateReader
{
    public string Read(string originalPackageRoot, CancellationToken cancellationToken)
    {
        using WindowsMaintenancePackageStateLease lease = WindowsMaintenancePackageStateLease.Acquire(originalPackageRoot, cancellationToken);
        return lease.ContentsSha256;
    }
}
