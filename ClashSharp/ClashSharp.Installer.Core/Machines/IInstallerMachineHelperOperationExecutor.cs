namespace ClashSharp.Installer.Machines;

/// <summary>Executes or independently re-verifies one fixed privileged helper operation.</summary>
public interface IInstallerMachineHelperOperationExecutor
{
    /// <summary>
    /// Executes the fixed command or verifies its already-committed postcondition.
    /// </summary>
    /// <remarks>
    /// Implementations must throw <see cref="Contracts.InstallerProtocolException"/> only for a
    /// stable, independently classified failure. Any possible unconfirmed side effect must instead
    /// throw <see cref="Contracts.InstallerStateUncertainException"/>.
    /// </remarks>
    Task ExecuteAsync(
        InstallerMachineHelperCommand command,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken);
}
