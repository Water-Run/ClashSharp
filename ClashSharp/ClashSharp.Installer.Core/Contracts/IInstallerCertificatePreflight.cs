namespace ClashSharp.Installer.Contracts;

/// <summary>Checks certificate trust and ownership without changing certificates or ownership ledgers.</summary>
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

    /// <summary>Rejects ownership or identity conflicts before authorizing application or service removal.</summary>
    /// <param name="request">Authenticated uninstall request.</param>
    /// <param name="release">Independently verified candidate identity; uninstall does not require certificate payload bytes.</param>
    /// <param name="cancellationToken">Cancels read-only inspection.</param>
    Task VerifyCanUninstallAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);
}
