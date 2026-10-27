namespace ClashSharp.Installer.Contracts;

/// <summary>Trusted first-install path that owns applying current-user preferences.</summary>
public interface IInstallerPreferencesRuntimeBackend
{
    /// <summary>Whether all offered installation preferences are supported by the composition.</summary>
    bool SupportsInstallPreferences { get; }

    /// <summary>Installs the exact release and applies one immutable preference snapshot.</summary>
    Task<InstallerExecutionResult> InstallWithPreferencesAsync(InstallerPreferences preferences,
        IProgress<InstallerProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Completes a correlated user-only intent after the core installation has been verified.</summary>
    Task<InstallerExecutionResult> CompletePreferencesAsync(IProgress<InstallerProgress>? progress, CancellationToken cancellationToken);
}
