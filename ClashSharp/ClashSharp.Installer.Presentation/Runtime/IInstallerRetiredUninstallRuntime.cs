using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Runtime;

/// <summary>Optional account-copy uninstall action, invoked only after explicit shell confirmation.</summary>
public interface IInstallerRetiredUninstallRuntime
{
    /// <summary>Gets whether the trusted composition supplies this distinct action.</summary>
    bool SupportsRetiredUninstall { get; }

    /// <summary>Removes the authenticated retired account's copy while preserving the shared installation.</summary>
    Task<InstallerExecutionResult> UninstallRetiredAccountAsync(
        IProgress<InstallerProgress> progress, CancellationToken cancellationToken);
}
