using ClashSharp.Installer.Machines;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperBroker
{
    /// <summary>
    /// Executes one canonical journal-bearing command. Implementations reuse the same authenticated helper
    /// process and pipe for a transaction, so one normal operation crosses UAC at most once.
    /// </summary>
    Task<InstallerMachineHelperResult> ExecuteAsync(
        InstallerMachineHelperCommand command);
}
