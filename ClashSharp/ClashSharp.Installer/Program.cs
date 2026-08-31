using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer;

internal static class Program
{
    private const int InvalidMachineHelperArgumentsExitCode = 2;
    private const int MachineHelperFailedExitCode = 3;

    [STAThread]
    internal static int Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return InstallerStartupRouter.Run(
            arguments,
            runMachineHelper: RunMachineHelper,
            runUserInterface: static () =>
            {
                var application = new App();
                application.InitializeComponent();
                return application.Run();
            },
            invalidArgumentsExitCode: InvalidMachineHelperArgumentsExitCode);
    }

    private static int RunMachineHelper(InstallerMachineHelperBootstrap bootstrap)
    {
#if CLASHSHARP_INSTALLER_MUTATION_RUNTIME
        return RunEnabledMachineHelper(bootstrap);
#else
        _ = bootstrap;
        return MachineHelperFailedExitCode;
#endif
    }

    // Kept outside the conditional so default builds still compile the complete production
    // integration while the startup route itself remains impossible to enter.
    private static int RunEnabledMachineHelper(InstallerMachineHelperBootstrap bootstrap)
    {
        try
        {
            EmbeddedInstallerReleaseManifest release = EmbeddedInstallerReleaseManifest.Load();
            string executablePath = Environment.ProcessPath
                ?? throw new InstallerProtocolException(
                    "installer.machine_helper.executable_path_missing");
            WindowsInstallerMachineHelper
                .RunAsync(
                    bootstrap,
                    executablePath,
                    release.Bytes,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return 0;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return MachineHelperFailedExitCode;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}
