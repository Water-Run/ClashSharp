using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Packages;

/// <summary>
/// Provides exact current-user package registration and deployment operations to the Core policy.
/// </summary>
public interface IInstallerPackageStoreAdapter
{
    /// <summary>
    /// Returns the one exact product-family registration, or <see langword="null"/> when absent.
    /// Ambiguous or foreign identities must fail rather than be collapsed into absence.
    /// </summary>
    Task<InstallerInstalledPackage?> InspectAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);

    /// <summary>Deploys or repairs the locked primary package and exact locked dependencies.</summary>
    Task DeployAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);

    /// <summary>Removes only the exact registration returned by a preceding inspection.</summary>
    Task RemoveAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerInstalledPackage installedPackage,
        CancellationToken cancellationToken);
}
