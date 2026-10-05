namespace ClashSharp.Installer.Machines;

/// <summary>
/// Required elevated capability for original-installation recovery. Implementations retain original
/// evidence and authority until mutation, independent verification, and terminal reconciliation finish.
/// Ordinary candidate verification cannot satisfy this contract.
/// </summary>
public interface IInstallerMachineHelperOriginalRestoration
{
    /// <summary>
    /// Restores the original installation or verifies an already committed restoration. ClearOriginal
    /// additionally prepares its private completion evidence before the public terminal is removed.
    /// Committed replays never repeat service mutations.
    /// </summary>
    Task ExecuteOriginalRestorationAsync(InstallerMachineHelperCommand command,
        InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken);
}
