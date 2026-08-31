using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerMachineHelperTests
{
    [Fact]
    public async Task MalformedEmbeddedManifestFailsBeforeElevationOrPipeAccess()
    {
        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => WindowsInstallerMachineHelper.RunAsync(
                Bootstrap(),
                @"C:\Release\ClashSharp.Installer.exe",
                "{}"u8.ToArray(),
                CancellationToken.None));

        Assert.Equal("installer.release.manifest_json_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public async Task PreCancellationWinsBeforeManifestParsingOrElevation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsInstallerMachineHelper.RunAsync(
                Bootstrap(),
                @"C:\Release\ClashSharp.Installer.exe",
                "{}"u8.ToArray(),
                cancellation.Token));
    }

    private static InstallerMachineHelperBootstrap Bootstrap()
    {
        var request = new InstallerRequest(
            InstallerOperation.Install,
            "S-1-5-21-100-200-300-1001",
            AllowReassociation: false,
            "1.2.3.4",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        InstallerTransactionSnapshot prepared = InstallerTransactionSnapshot.Create(
            InstallerTransactionJournal.Create(request));
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Prepare,
                prepared);
        return InstallerMachineHelperBootstrap.Create(invocation, parentProcessId: 4242);
    }
}
