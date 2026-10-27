using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Settings;
using Microsoft.Win32.SafeHandles;

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
        await ObserveProcessAsync(processId, expectedExecutable, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ObserveProcessAsync(uint processId, string expectedExecutable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process process;
        try { process = Process.GetProcessById(checked((int)processId)); }
        catch (ArgumentException)
        {
            // A fast child may have already exited. Its package-local receipt is the authority.
            return;
        }
        using (process)
        {
            string executable;
            uint sessionId;
            try
            {
                // GetProcessById does not retain a native handle. Keep one before polling:
                // a fast windowless bootstrap can disappear from the process table before
                // ExitCode is read even though HasExited already returned true.
                _ = process.SafeHandle;
                // The kernel image name is available before the child has finished loading
                // its module list, including windowless processes that immediately exit.
                char[] path = new char[32768];
                uint length = (uint)path.Length;
                if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length)
                    || !ProcessIdToSessionId(processId, out sessionId))
                {
                    if (process.HasExited) { return; }
                    throw Failure("bootstrap_process_unconfirmed");
                }
                executable = new string(path, 0, checked((int)length));
            }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            catch (Win32Exception) when (process.HasExited) { return; }
            using Process current = Process.GetCurrentProcess();
            if (!string.Equals(executable, expectedExecutable, StringComparison.OrdinalIgnoreCase)
                || sessionId != (uint)current.SessionId)
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

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 3)] char[] path, ref uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

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
