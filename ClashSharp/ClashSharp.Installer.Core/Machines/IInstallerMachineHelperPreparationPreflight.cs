namespace ClashSharp.Installer.Machines;

/// <summary>Inspects install or repair prerequisites within authenticated helper authority.</summary>
public interface IInstallerMachineHelperPreparationPreflight
{
    /// <summary>
    /// Checks the exact candidate without certificate, service, package, or journal mutation.
    /// The authority session invokes this before persisting a new Prepared intent; execution
    /// must revalidate external state before performing its effects.
    /// </summary>
    /// <param name="command">Validated Prepare command for installation or repair.</param>
    /// <param name="cancellationToken">Cancels read-only preflight.</param>
    Task VerifyPreparationAsync(
        InstallerMachineHelperCommand command,
        CancellationToken cancellationToken);
}
