using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Certificates;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsCurrentUserCertificateStoreAdapterTests
{
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
