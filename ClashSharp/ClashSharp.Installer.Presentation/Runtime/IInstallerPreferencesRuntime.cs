using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Runtime;

/// <summary>Exposes installation choices without allowing presentation to supply mutation authority.</summary>
public interface IInstallerPreferencesRuntime
{
    /// <summary>Whether the current production backend applies every offered option.</summary>
    bool SupportsInstallPreferences { get; }

    /// <summary>Installs with the current user's validated choices.</summary>
    Task<InstallerExecutionResult> InstallWithPreferencesAsync(InstallerPreferences preferences,
        IProgress<InstallerProgress> progress, CancellationToken cancellationToken);

    /// <summary>Finishes pending user setup without repeating package or system-service mutation.</summary>
    Task<InstallerExecutionResult> CompletePreferencesAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken);
}
