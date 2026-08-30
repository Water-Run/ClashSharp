using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Certificates;

/// <summary>Describes the exact result of inspecting the target user's trusted certificate store.</summary>
public enum InstallerCertificatePresence
{
    /// <summary>No certificate with the verified release thumbprint is present.</summary>
    Missing,

    /// <summary>The thumbprint and full DER SHA-256 both match the verified release certificate.</summary>
    ExactMatch,

    /// <summary>The thumbprint exists but its full DER SHA-256 does not match.</summary>
    IdentityConflict,
}

/// <summary>Provides exact, target-user certificate-store operations to the durable core.</summary>
public interface IInstallerCertificateStoreAdapter
{
    /// <summary>Inspects the fixed CurrentUser/TrustedPeople location without mutating it.</summary>
    Task<InstallerCertificatePresence> InspectAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);

    /// <summary>Imports the verified certificate payload and verifies its exact resulting identity.</summary>
    Task ImportAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);

    /// <summary>Removes only the exact thumbprint and DER SHA-256 identity, then verifies absence.</summary>
    Task RemoveExactAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);
}
