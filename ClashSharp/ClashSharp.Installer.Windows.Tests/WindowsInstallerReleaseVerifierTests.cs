using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerReleaseVerifierTests
{
    [Fact]
    public async Task UninstallDoesNotRequireOrOpenSiblingPayload()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        var verifier = new WindowsInstallerReleaseVerifier(
            fixture.ManifestBytes,
            fixture.ExecutablePath);
        InstallerRequest request = fixture.Request(InstallerOperation.Uninstall);

        await using IInstallerReleaseLease lease = await verifier.VerifyAsync(
            request,
            CancellationToken.None);

        Assert.False(lease.Release.PackagePayloadAvailable);
        Assert.False(lease.Release.CertificatePayloadAvailable);
        Assert.Empty(lease.LockedFiles);
        await lease.ReverifyAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task InstallFailsClosedWhenPayloadRootIsMissing()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        var verifier = new WindowsInstallerReleaseVerifier(
            fixture.ManifestBytes,
            fixture.ExecutablePath);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(fixture.Request(), CancellationToken.None));

        Assert.Equal("installer.release.payload_lock_failed", exception.DiagnosticCode);
    }

    [Fact]
    public async Task RequestIdentityMismatchIsRejectedBeforePayloadAccess()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        var verifier = new WindowsInstallerReleaseVerifier(
            fixture.ManifestBytes,
            fixture.ExecutablePath);
        InstallerRequest request = fixture.Request() with
        {
            ExpectedPackageVersion = "1.2.3.5",
        };

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(request, CancellationToken.None));

        Assert.Equal("installer.release.identity_mismatch", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("ClashSharp.exe")]
    [InlineData("ClashSharp.Installer.dll")]
    [InlineData("installer.exe")]
    public void ExecutableNameMustMatchThePublishedInstaller(string executableName)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        string wrongPath = Path.Combine(fixture.RootDirectory, executableName);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsInstallerReleaseVerifier(fixture.ManifestBytes, wrongPath));

        Assert.Equal("installer.release.executable_path_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void EmptyEmbeddedManifestIsRejected()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsInstallerReleaseVerifier(
                ReadOnlyMemory<byte>.Empty,
                fixture.ExecutablePath));

        Assert.Equal("installer.release.manifest_missing", exception.DiagnosticCode);
    }
}
