namespace ClashSharp.Installer.Contracts;

/// <summary>Optional trusted backend for explicitly requested removal of a retired account's copy.</summary>
public interface IInstallerRetiredUninstallRuntimeBackend
{
    /// <summary>Gets whether the separately authenticated account-copy removal is available.</summary>
    bool SupportsRetiredUninstall { get; }

    /// <summary>Removes or resumes removal of this account's package and archived owned trust.</summary>
    Task<InstallerExecutionResult> UninstallRetiredAccountAsync(
        IProgress<InstallerProgress>? progress, CancellationToken cancellationToken);
}
