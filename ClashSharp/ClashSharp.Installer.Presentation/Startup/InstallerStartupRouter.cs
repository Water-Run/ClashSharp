using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Presentation;

/// <summary>Routes one process launch before any WPF or privileged composition is created.</summary>
public static class InstallerStartupRouter
{
    /// <summary>
    /// Parses reserved audit and machine-helper grammar and invokes exactly one composition branch.
    /// </summary>
    /// <param name="arguments">Raw process arguments.</param>
    /// <param name="runMachineHelper">Privileged helper composition.</param>
    /// <param name="runUserInterface">Ordinary WPF composition.</param>
    /// <param name="invalidArgumentsExitCode">Stable exit code for invalid reserved grammar.</param>
    /// <param name="runPayloadAudit">Optional read-only audit composition without WPF or elevation.</param>
    /// <param name="runOwnerTransfer">Optional dedicated ownership helper composition.</param>
    /// <returns>The selected branch exit code, or the invalid-arguments exit code.</returns>
    public static int Run(
        IReadOnlyList<string> arguments,
        Func<InstallerMachineHelperBootstrap, int> runMachineHelper,
        Func<int> runUserInterface,
        int invalidArgumentsExitCode,
        Func<int>? runPayloadAudit = null,
        Func<InstallerOwnerTransferBootstrap, int>? runOwnerTransfer = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runMachineHelper);
        ArgumentNullException.ThrowIfNull(runUserInterface);

        // Reserve lookalikes and mixed modes too, so a mistyped audit cannot open the installer UI.
        if (arguments.Any(static argument =>
            argument?.StartsWith("--verify-payload", StringComparison.OrdinalIgnoreCase) == true))
        {
            return arguments.Count == 1
                && string.Equals(arguments[0], "--verify-payload", StringComparison.Ordinal)
                && runPayloadAudit is not null
                    ? runPayloadAudit()
                    : invalidArgumentsExitCode;
        }

        InstallerMachineHelperBootstrap? bootstrap;
        InstallerOwnerTransferBootstrap? transfer;
        try
        {
            transfer = InstallerOwnerTransferBootstrap.Parse(arguments);
            if (transfer is not null)
            {
                return runOwnerTransfer is null ? invalidArgumentsExitCode : runOwnerTransfer(transfer);
            }
            bootstrap = InstallerMachineHelperBootstrap.Parse(arguments);
        }
        catch (InstallerProtocolException)
        {
            return invalidArgumentsExitCode;
        }

        return bootstrap is null
            ? runUserInterface()
            : runMachineHelper(bootstrap);
    }
}
