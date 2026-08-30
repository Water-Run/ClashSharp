using ClashSharp.Installer.Presentation;

namespace ClashSharp.Installer;

internal static class Program
{
    private const int InvalidMachineHelperArgumentsExitCode = 2;
    private const int MachineHelperNotConnectedExitCode = 3;

    [STAThread]
    internal static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return InstallerStartupRouter.Run(
            arguments,
            runMachineHelper: static _ => MachineHelperNotConnectedExitCode,
            runUserInterface: static () =>
            {
                var application = new App();
                application.InitializeComponent();
                return application.Run();
            },
            invalidArgumentsExitCode: InvalidMachineHelperArgumentsExitCode);
    }
}
