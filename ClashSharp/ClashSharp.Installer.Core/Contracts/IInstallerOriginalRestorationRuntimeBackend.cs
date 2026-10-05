namespace ClashSharp.Installer.Contracts;

/// <summary>Trusted platform entry for preserving an exact pending same-owner installation.</summary>
public interface IInstallerOriginalRestorationRuntimeBackend
{
    /// <summary>Gets whether this backend supplies authenticated original recovery.</summary>
    bool SupportsOriginalRestoration { get; }

    /// <summary>Reconstructs the exact repair request and restores or completes its original installation.</summary>
    Task<InstallerExecutionResult> RestoreOriginalAsync(IProgress<InstallerProgress>? progress, CancellationToken cancellationToken);
}
