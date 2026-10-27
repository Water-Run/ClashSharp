using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Settings;

namespace ClashSharp.Installer.Windows.Execution;

internal interface IWindowsPackagedSetupLauncher
{
    Task RunAsync(string applicationId, string expectedExecutable, string requestId, CancellationToken cancellationToken);
}

/// <summary>Activates the exact registered package for a bounded, user-only setup handoff.</summary>
internal sealed class WindowsPackagedSetupLauncher : IWindowsPackagedSetupLauncher
{
    public async Task RunAsync(string applicationId, string expectedExecutable, string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object native = new ApplicationActivationManager();
        uint processId;
        try
        {
            ((IApplicationActivationManager)native).ActivateApplication(applicationId,
                InstallationSetupProtocol.BootstrapArgument + " " + requestId, 6, out processId);
        }
        finally { Marshal.FinalReleaseComObject(native); }
        if (processId == 0) { throw Failure("bootstrap_unconfirmed"); }
        Process process;
        try { process = Process.GetProcessById(checked((int)processId)); }
        catch (ArgumentException)
        {
            // A fast child may have already exited. Its package-local receipt is the authority.
            return;
        }
        using (process)
        {
            string? executable;
            try { executable = process.MainModule?.FileName; }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            catch (Win32Exception) when (process.HasExited) { return; }
            using Process current = Process.GetCurrentProcess();
            if (!string.Equals(executable, expectedExecutable, StringComparison.OrdinalIgnoreCase)
                || process.SessionId != current.SessionId)
            {
                throw Failure("bootstrap_process_mismatch");
            }
            var timer = Stopwatch.StartNew();
            // No unowned WaitForExit task survives cancellation. Do not terminate a package
            // process on a timeout: pending intent and the correlated receipt retain recovery.
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed > TimeSpan.FromSeconds(30)) { throw Failure("bootstrap_unconfirmed"); }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            if (process.ExitCode != 0) { throw Failure("bootstrap_failed"); }
        }
    }

    private static InstallerProtocolException Failure(string code) => new("installer.preferences." + code);

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private sealed class ApplicationActivationManager { }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string applicationId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        void ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string applicationId, nint items,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        void ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string applicationId, nint items, out uint processId);
    }
}
