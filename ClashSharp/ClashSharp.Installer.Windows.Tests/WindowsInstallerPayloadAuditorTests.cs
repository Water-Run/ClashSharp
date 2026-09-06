using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerPayloadAuditorTests
{
    [Theory]
    [InlineData("payload")]
    [InlineData(@"\\server\release\payload")]
    [InlineData("//server/release/payload")]
    [InlineData(@"\\?\C:\release\payload")]
    public void NonlocalOrRelativePathsAreRejectedBeforeManifestOrPayloadAccess(string payloadRoot)
    {
        WindowsPayloadFixture.AssertWindows11X64();

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsInstallerPayloadAuditor(ReadOnlyMemory<byte>.Empty, payloadRoot));

        Assert.Equal("installer.release.payload_path_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ValidPayloadReturnsEvidenceAndReleasesEveryFileHandle()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        var auditor = new WindowsInstallerPayloadAuditor(fixture.ManifestBytes, fixture.PayloadRoot);

        InstallerPayloadAuditResult result = await auditor.AuditAsync(CancellationToken.None);

        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, result.PackageVersion);
        Assert.Equal(fixture.Manifest.InstallerPayloadSha256, result.PayloadSha256);
        Assert.Equal(fixture.Manifest.Files.Count, result.FileCount);
        Assert.Equal(fixture.Manifest.Files.Sum(static file => file.Length), result.TotalBytes);
        Assert.Equal(fixture.Manifest.MachineFiles.Count, result.MachineFileCount);
        foreach (InstallerPayloadFileEntry file in fixture.Manifest.Files)
        {
            using var stream = new FileStream(
                Path.Combine(fixture.PayloadRoot, file.Path),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Assert.Equal(file.Length, stream.Length);
        }
    }

    [Fact]
    public async Task SameLengthTamperingFailsAndReleasesHandles()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        byte[] bytes = File.ReadAllBytes(fixture.PrimaryPath);
        bytes[^1] ^= 1;
        File.WriteAllBytes(fixture.PrimaryPath, bytes);
        var auditor = new WindowsInstallerPayloadAuditor(fixture.ManifestBytes, fixture.PayloadRoot);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => auditor.AuditAsync(CancellationToken.None));

        Assert.Equal("installer.release.locked_file_hash_mismatch", exception.DiagnosticCode);
        using var writable = new FileStream(fixture.PrimaryPath, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Fact]
    public async Task UnexpectedSiblingIsRejected()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        File.WriteAllBytes(Path.Combine(fixture.PayloadRoot, "unexpected.bin"), [1]);
        var auditor = new WindowsInstallerPayloadAuditor(fixture.ManifestBytes, fixture.PayloadRoot);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => auditor.AuditAsync(CancellationToken.None));

        Assert.Equal("installer.release.payload_file_set_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public async Task CancelledAuditDoesNotOpenMissingPayload()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);
        var auditor = new WindowsInstallerPayloadAuditor(fixture.ManifestBytes, fixture.PayloadRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auditor.AuditAsync(cancellation.Token));
        Assert.False(Directory.Exists(fixture.PayloadRoot));
    }
}
