using System.IO;
using System.Text.Json;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;
using ClashSharp.Installer.Windows.Files;
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
            invalidArgumentsExitCode: InvalidMachineHelperArgumentsExitCode,
            runPayloadAudit: RunPayloadAudit);
    }

    private static int RunPayloadAudit()
    {
        try
        {
            EmbeddedInstallerReleaseManifest release = EmbeddedInstallerReleaseManifest.Load();
            string executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)
                ?? throw new InstallerProtocolException("installer.release.executable_path_invalid");
            var auditor = new WindowsInstallerPayloadAuditor(
                release.Bytes,
                Path.Combine(executableDirectory, "payload"));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            InstallerPayloadAuditResult result = auditor.AuditAsync(cancellation.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status = "passed",
                publishedExecutable = InstallerArtifactNames.PublishedExecutable,
                packageVersion = result.PackageVersion,
                payloadSha256 = result.PayloadSha256,
                fileCount = result.FileCount,
                totalBytes = result.TotalBytes,
                machineFileCount = result.MachineFileCount,
            }));
            return 0;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Keep process diagnostics bounded and free of paths, identities, and exception text.
            Console.WriteLine("{\"schemaVersion\":1,\"status\":\"failed\",\"code\":\"installer.release.payload_audit_failed\"}");
            return MachineHelperFailedExitCode;
        }
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
