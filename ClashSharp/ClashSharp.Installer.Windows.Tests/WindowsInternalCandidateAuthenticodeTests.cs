using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInternalCandidateAuthenticodeTests
{
    [Theory]
    [InlineData(-60, true)]
    [InlineData(0, false)]
    [InlineData(-96, false)]
    public void InternalChainUsesTheWindowsVerifiedTimeInsteadOfDownloadTime(int hoursFromNow, bool accepted)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ClashSharp Internal Candidate " + Guid.NewGuid().ToString("N"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-3), now.AddDays(-2));

        Assert.Equal(accepted, WindowsAuthenticodeVerifier.IsValidInternalCertificate(certificate, now.AddHours(hoursFromNow).UtcDateTime));
    }

    [Fact]
    public async Task PinnedInternalCandidateWorksWithoutInstallingItsRoot()
    {
        using var fixture = new UntrustedSignedFixture();
        string[] rootsBefore = UntrustedSignedFixture.ReadRoots();
        var verifier = fixture.Verifier(allowInternal: true);

        using (IWindowsInstallerExecutableTrustLease lease = await verifier.VerifyAsync(fixture.Path, CancellationToken.None))
        {
            Assert.Equal(fixture.Path, lease.ExecutablePath);
            Assert.Throws<IOException>(() => File.OpenWrite(fixture.Path).Dispose());
        }

        Assert.DoesNotContain(fixture.Thumbprint, rootsBefore);
        Assert.DoesNotContain(fixture.Thumbprint, UntrustedSignedFixture.ReadRoots());
    }

    [Fact]
    public async Task NormalReleasePolicyStillRequiresSystemTrust()
    {
        using var fixture = new UntrustedSignedFixture();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(allowInternal: false).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }

    [Fact]
    public async Task InternalPolicyRejectsAValidSignatureFromAnotherPinnedIdentity()
    {
        using var fixture = new UntrustedSignedFixture();
        string different = (fixture.Thumbprint[0] == 'A' ? "B" : "A") + fixture.Thumbprint[1..];
        var verifier = new WindowsInstallerExecutableTrustVerifier(different, new WindowsAuthenticodeVerifier(true));

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.signer_mismatch", failure.DiagnosticCode);
    }

    [Fact]
    public async Task UntrustedRootCannotMaskModifiedExecutableContents()
    {
        using var fixture = new UntrustedSignedFixture();
        fixture.ModifyCode();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(allowInternal: true).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.authenticode_invalid", failure.DiagnosticCode);
    }

    [Fact]
    public async Task InternalPolicyDoesNotAdmitAnArbitrarySelfSignedPublisher()
    {
        using var fixture = new UntrustedSignedFixture("CN=Other Untrusted Publisher");

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(allowInternal: true).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }
}
