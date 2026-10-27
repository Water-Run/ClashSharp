using System.Diagnostics;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsPackagedSetupLauncherTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RetainsTheExternalProcessHandleUntilItsExitCodeIsObserved(int exitCode)
    {
        using Process child = StartBoundedChild(exitCode);
        try
        {
            Task observed = WindowsPackagedSetupLauncher.ObserveProcessAsync((uint)child.Id, PowerShellPath, CancellationToken.None);
            if (exitCode == 0) { await observed; }
            else
            {
                InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() => observed);
                Assert.Equal("installer.preferences.bootstrap_failed", failure.DiagnosticCode);
            }
        }
        finally { await child.WaitForExitAsync(); }
        Assert.Equal(exitCode, child.ExitCode);
    }

    [Fact]
    public async Task IdentityMismatchDoesNotTerminateTheObservedProcess()
    {
        using Process child = StartBoundedChild(7);
        try
        {
            InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                WindowsPackagedSetupLauncher.ObserveProcessAsync((uint)child.Id, Path.Combine(Path.GetTempPath(), "not-the-child.exe"), CancellationToken.None));
            Assert.Equal("installer.preferences.bootstrap_process_mismatch", failure.DiagnosticCode);
        }
        finally { await child.WaitForExitAsync(); }
        Assert.Equal(7, child.ExitCode);
    }

    [Fact]
    public async Task CancelledObservationDoesNotOpenOrWaitForAProcess()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsPackagedSetupLauncher.ObserveProcessAsync((uint)Environment.ProcessId, PowerShellPath, cancelled.Token));
    }

    private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static Process StartBoundedChild(int exitCode)
    {
        ProcessStartInfo start = new(PowerShellPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", $"Start-Sleep -Milliseconds 300; exit {exitCode}" })
        { start.ArgumentList.Add(argument); }
        return Process.Start(start) ?? throw new InvalidOperationException("The bounded native child did not start.");
    }
}
