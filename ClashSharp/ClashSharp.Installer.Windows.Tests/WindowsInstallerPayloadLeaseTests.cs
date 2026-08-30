using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerPayloadLeaseTests
{
    [Fact]
    public async Task LeasePinsExactFilesAndDirectoryNamesUntilDisposed()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();

        await using var lease = fixture.Lock(request);
        Assert.Equal(fixture.Manifest.Files.Count, lease.LockedFiles.Count);
        await lease.ReverifyAsync(request, CancellationToken.None);

        Assert.ThrowsAny<IOException>(() =>
        {
            using FileStream stream = new(
                fixture.PrimaryPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.ThrowsAny<IOException>(() => Directory.Move(
            fixture.PayloadRoot,
            fixture.PayloadRoot + "-renamed"));
    }

    [Fact]
    public async Task ReverifyRejectsAnUnexpectedSiblingCreatedAfterLock()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using var lease = fixture.Lock(request);
        string unexpectedPath = Path.Combine(fixture.PayloadRoot, "unexpected.bin");
        File.WriteAllBytes(unexpectedPath, [0x01]);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => lease.ReverifyAsync(request, CancellationToken.None));

        Assert.Equal("installer.release.payload_file_set_invalid", exception.DiagnosticCode);
        File.Delete(unexpectedPath);
    }

    [Fact]
    public async Task ReverifyRejectsAnyChangedRequestIdentity()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using var lease = fixture.Lock(request);
        InstallerRequest changed = fixture.Request(InstallerOperation.Repair);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => lease.ReverifyAsync(changed, CancellationToken.None));

        Assert.Equal("installer.release.request_changed", exception.DiagnosticCode);
    }

    [Fact]
    public void LockRejectsAnUnexpectedFileBeforeReturningAnyLease()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        File.WriteAllBytes(Path.Combine(fixture.PayloadRoot, "unexpected.bin"), [0x01]);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            fixture.Lock());

        Assert.Equal("installer.release.payload_file_set_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void LockRejectsPrimaryMsixIdentityThatDisagreesWithEmbeddedManifest()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(primaryPackageNameOverride: "Contoso.Other");

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            fixture.Lock());

        Assert.Equal("installer.release.package_identity_mismatch", exception.DiagnosticCode);
    }

    [Fact]
    public void LockRejectsDependencyThatIsNotAFrameworkPackage()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(dependencyIsFramework: false);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            fixture.Lock());

        Assert.Equal("installer.release.dependency_identity_mismatch", exception.DiagnosticCode);
    }
}
