using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Runtime;

/// <summary>Presentation entry that requests recovery without choosing privileged identities or paths.</summary>
public interface IInstallerOriginalRestorationRuntime
{
    /// <summary>Gets whether the trusted backend provides original recovery.</summary>
    bool SupportsOriginalRestoration { get; }

    /// <summary>Attempts to preserve the original pending installation after independent verification.</summary>
    Task<InstallerExecutionResult> RestoreOriginalAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken);
}
