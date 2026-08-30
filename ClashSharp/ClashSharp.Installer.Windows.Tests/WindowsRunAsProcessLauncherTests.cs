using System.ComponentModel;
using System.Diagnostics;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsRunAsProcessLauncherTests
{
    [Fact]
    public async Task LaunchUsesExactSelfPathRunasAndArgumentListOnSta()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        ProcessStartInfo? captured = null;
        ApartmentState apartment = ApartmentState.Unknown;
        using var returnedProcess = new Process();
        var launcher = new WindowsRunAsProcessLauncher(startInfo =>
        {
            captured = startInfo;
            apartment = Thread.CurrentThread.GetApartmentState();
            return returnedProcess;
        });
        InstallerMachineHelperBootstrap bootstrap = Bootstrap();
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Installer.MachineHelper.exe");

        Process actual = await launcher.StartAsync(
            executablePath,
            bootstrap,
            CancellationToken.None);

        Assert.Same(returnedProcess, actual);
        Assert.NotNull(captured);
        Assert.Equal(Path.GetFullPath(executablePath), captured.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("runas", captured.Verb);
        Assert.False(captured.CreateNoWindow);
        Assert.False(captured.ErrorDialog);
        Assert.Equal(ProcessWindowStyle.Hidden, captured.WindowStyle);
        Assert.Equal(bootstrap.ToArguments().ToArray(), captured.ArgumentList.ToArray());
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.Equal(string.Empty, captured.WorkingDirectory);
        Assert.Equal(string.Empty, captured.Arguments);
    }

    [Fact]
    public async Task ExplicitUacCancellationHasADistinctStableOutcome()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var launcher = new WindowsRunAsProcessLauncher(
            static _ => throw new Win32Exception(1223, "sensitive shell text"));

        InstallerUserCancelledException exception =
            await Assert.ThrowsAsync<InstallerUserCancelledException>(() => launcher.StartAsync(
                ExecutablePath(),
                Bootstrap(),
                CancellationToken.None));

        Assert.Equal("installer.elevation.user_cancelled", exception.DiagnosticCode);
        Assert.DoesNotContain("sensitive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCancelledRequestNeverCallsShellExecute()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        int calls = 0;
        var launcher = new WindowsRunAsProcessLauncher(_ =>
        {
            calls++;
            return new Process();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.StartAsync(
            ExecutablePath(),
            Bootstrap(),
            cancellation.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RecoverableLaunchFailureIsSanitizedButFatalFailurePropagates()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var recoverable = new WindowsRunAsProcessLauncher(
            static _ => throw new IOException("secret path"));
        InstallerProtocolException sanitized =
            await Assert.ThrowsAsync<InstallerProtocolException>(() => recoverable.StartAsync(
                ExecutablePath(),
                Bootstrap(),
                CancellationToken.None));
        Assert.Equal("installer.elevation.launch_failed", sanitized.DiagnosticCode);

        var fatal = new WindowsRunAsProcessLauncher(
            static _ => throw new FatalTestException("sentinel"));
        await Assert.ThrowsAsync<FatalTestException>(() => fatal.StartAsync(
            ExecutablePath(),
            Bootstrap(),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(@"C:\release\installer.exe")]
    [InlineData(@"C:\release\ClashSharp.Installer.exe")]
    [InlineData(@"ClashSharp.Installer.MachineHelper.exe")]
    [InlineData(@"\\server\release\ClashSharp.Installer.MachineHelper.exe")]
    [InlineData(@"\\?\C:\release\ClashSharp.Installer.MachineHelper.exe")]
    public async Task NoncanonicalExecutablePathFailsBeforeShellExecute(string executablePath)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        int calls = 0;
        var launcher = new WindowsRunAsProcessLauncher(_ =>
        {
            calls++;
            return new Process();
        });

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() => launcher.StartAsync(
                executablePath,
                Bootstrap(),
                CancellationToken.None));

        Assert.Equal("installer.elevation.executable_path_invalid", exception.DiagnosticCode);
        Assert.Equal(0, calls);
    }

    private static string ExecutablePath() => Path.Combine(
        Path.GetTempPath(),
        "ClashSharp.Installer.MachineHelper.exe");

    private static InstallerMachineHelperBootstrap Bootstrap()
    {
        InstallerRequest request = new(
            InstallerOperation.Install,
            "S-1-5-21-100-200-300-1001",
            AllowReassociation: false,
            "1.2.3.4",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Prepare,
                InstallerTransactionSnapshot.Create(journal));
        return InstallerMachineHelperBootstrap.Create(invocation, parentProcessId: 4242);
    }
}
