namespace ClashSharp.Installer.Contracts;

/// <summary>Checks installation trust and ownership without importing certificates or writing ledgers.</summary>
public interface IInstallerCertificatePreflight
{
    /// <summary>Rejects known trust conflicts before any package or service maintenance begins.</summary>
    /// <param name="request">Authenticated install or repair request.</param>
    /// <param name="release">Independently verified candidate release lease.</param>
    /// <param name="cancellationToken">Cancels read-only inspection.</param>
    Task VerifyCanInstallAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);
}
