using ClashSharp.Installer.Contracts;
using Windows.Management.Deployment;

namespace ClashSharp.Installer.Windows.Packages;

/// <summary>Reads the exact globally installed package identity under elevated helper authority.</summary>
internal sealed class WindowsInstalledPackageFootprintCatalog : IWindowsInstalledPackageFootprintCatalog
{
    private readonly PackageManager _packages = new();

    public IReadOnlyList<string> FindInstalledRoots(string packageFamilyName, string packageFullName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        cancellationToken.ThrowIfCancellationRequested();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (global::Windows.ApplicationModel.Package package in _packages.FindPackages(packageFamilyName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(package.Id.FamilyName, packageFamilyName, StringComparison.Ordinal))
                {
                    throw new InstallerProtocolException("installer.package.installed_identity_mismatch");
                }
                if (string.Equals(package.Id.FullName, packageFullName, StringComparison.Ordinal))
                {
                    if (package.IsBundle || package.IsDevelopmentMode || package.IsOptional || package.IsResourcePackage || package.IsStub)
                    {
                        throw new InstallerProtocolException("installer.package.installed_identity_mismatch");
                    }
                    roots.Add(package.InstalledLocation.Path);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return roots.ToArray();
        }
        catch (Exception exception) when (exception is not (InstallerProtocolException or OperationCanceledException
            or OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
        {
            throw new InstallerProtocolException("installer.package.inspection_failed", exception);
        }
    }
}
