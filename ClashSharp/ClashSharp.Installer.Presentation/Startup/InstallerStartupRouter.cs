using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;

namespace ClashSharp.Installer.Presentation;

/// <summary>Routes one process launch before any WPF or privileged composition is created.</summary>
public static class InstallerStartupRouter
{
    /// <summary>
    /// Parses the reserved machine-helper grammar and invokes exactly one composition branch.
    /// </summary>
    /// <param name="arguments">Raw process arguments.</param>
    /// <param name="runMachineHelper">Privileged helper composition.</param>
    /// <param name="runUserInterface">Ordinary WPF composition.</param>
    /// <param name="invalidArgumentsExitCode">Stable exit code for invalid helper grammar.</param>
    /// <returns>The selected branch exit code, or the invalid-arguments exit code.</returns>
    public static int Run(
        IReadOnlyList<string> arguments,
        Func<InstallerMachineHelperBootstrap, int> runMachineHelper,
        Func<int> runUserInterface,
        int invalidArgumentsExitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runMachineHelper);
        ArgumentNullException.ThrowIfNull(runUserInterface);

        InstallerMachineHelperBootstrap? bootstrap;
        try
        {
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
