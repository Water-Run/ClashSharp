using System.Security.Cryptography.X509Certificates;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Certificates;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsCurrentUserCertificateStoreAdapterTests
{
    public WindowsCurrentUserCertificateStoreAdapterTests()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        // Do not silently mutate a developer's trust stores when an overly broad test filter
        // selects this class. CI runs on the workflow's disposable GitHub-hosted Windows image.
        bool hostedCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted";
        bool sandbox = Environment.UserName == "WDAGUtilityAccount"
            && Environment.GetEnvironmentVariable("USERPROFILE") == @"C:\Users\WDAGUtilityAccount";
        Assert.True(hostedCi || sandbox, "Certificate mutation tests require disposable hosted CI or Windows Sandbox.");
    }

    [Theory]
    [InlineData("current")]
    [InlineData("target")]
    [InlineData("archive")]
    public async Task UserRemovalPreservesTheSameCertificateInTheMachineStore(string adapterKind)
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificateFromFile(fixture.CertificatePath);
        InstallerRequest install = fixture.Request();
        string thumbprint = fixture.Manifest.PackageCertificateThumbprint;
        string derSha256 = fixture.Manifest.CertificateSha256;
        using IWindowsCertificateStore machine = WindowsMachineCertificateStoreNative.Instance.Open(
            install.TargetSid, writable: true, createIfMissing: true)!;
        Assert.Equal(InstallerCertificatePresence.Missing,
            WindowsCertificateIdentity.InspectStore(machine, thumbprint, derSha256, CancellationToken.None));
        var targetStore = new WindowsTargetUserCertificateStoreAdapter();
        IInstallerCertificateStoreAdapter userStore = adapterKind == "current"
            ? new WindowsCurrentUserCertificateStoreAdapter() : targetStore;
        try
        {
            machine.AddEncodedCertificate(certificate.RawData);
            InstallerCertificateOwnershipLedger ledger;
            await using (var lease = fixture.Lock(install))
            {
                Assert.Equal(InstallerCertificatePresence.Missing,
                    await userStore.InspectAsync(install, lease, CancellationToken.None));
                await userStore.ImportAsync(install, lease, CancellationToken.None);
                Assert.Equal(InstallerCertificatePresence.ExactMatch,
                    await userStore.InspectAsync(install, lease, CancellationToken.None));
                ledger = InstallerCertificateOwnershipLedger.Create(install, lease.Release, certificateWasPresent: false)
                    .PrepareRemoval();
            }
            InstallerRequest uninstall = fixture.Request(InstallerOperation.Uninstall);
            await using var uninstallLease = fixture.Lock(uninstall);
            if (adapterKind == "archive")
            {
                await new WindowsArchivedCertificateRemovalAdapter(install.TargetSid)
                    .RemoveExactAsync(ledger, CancellationToken.None);
            }
            else
            {
                await userStore.RemoveExactAsync(uninstall, uninstallLease, CancellationToken.None);
            }
            Assert.Equal(InstallerCertificatePresence.Missing,
                await userStore.InspectAsync(uninstall, uninstallLease, CancellationToken.None));
            Assert.Equal(InstallerCertificatePresence.ExactMatch,
                WindowsCertificateIdentity.InspectStore(machine, thumbprint, derSha256, CancellationToken.None));
        }
        finally
        {
            try
            {
                using IWindowsCertificateStore? user = WindowsTargetUserCertificateStoreNative.Instance.Open(
                    install.TargetSid, writable: true, createIfMissing: false);
                _ = user?.DeleteExactCertificates(thumbprint, derSha256, CancellationToken.None);
            }
            finally
            {
                _ = machine.DeleteExactCertificates(thumbprint, derSha256, CancellationToken.None);
                Assert.Equal(InstallerCertificatePresence.Missing,
                    WindowsCertificateIdentity.InspectStore(machine, thumbprint, derSha256, CancellationToken.None));
            }
        }
    }

    [Fact]
    public async Task ExactCertificateRoundTripUsesOnlyCurrentUserTrustedPeople()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using var lease = fixture.Lock(request);
        var adapter = new WindowsCurrentUserCertificateStoreAdapter();

        Assert.Equal(
            InstallerCertificatePresence.Missing,
            await adapter.InspectAsync(request, lease, CancellationToken.None));

        await adapter.ImportAsync(request, lease, CancellationToken.None);
        Assert.Equal(
            InstallerCertificatePresence.ExactMatch,
            await adapter.InspectAsync(request, lease, CancellationToken.None));

        await adapter.RemoveExactAsync(request, lease, CancellationToken.None);
        Assert.Equal(
            InstallerCertificatePresence.Missing,
            await adapter.InspectAsync(request, lease, CancellationToken.None));
    }

    [Fact]
    public async Task AdapterRejectsARequestNotBoundToTheLease()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using var lease = fixture.Lock(request);
        var adapter = new WindowsCurrentUserCertificateStoreAdapter();
        InstallerRequest changed = fixture.Request(InstallerOperation.Repair);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.InspectAsync(changed, lease, CancellationToken.None));

        Assert.Equal("installer.release.request_changed", exception.DiagnosticCode);
    }

    [Fact]
    public async Task AdapterRejectsAValidButDifferentTargetUser()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request(
            targetSid: "S-1-5-21-100-200-300-1001");
        await using var lease = fixture.Lock(request);
        var adapter = new WindowsCurrentUserCertificateStoreAdapter();

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.InspectAsync(request, lease, CancellationToken.None));

        Assert.Equal("installer.certificate.target_user_mismatch", exception.DiagnosticCode);
    }
}
