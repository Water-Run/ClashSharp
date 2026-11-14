using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// The pinned self-signed release policy lets the maintainer's long-lived identity stand in for a public
/// root. These tests use the real Windows signature check on an owned signed executable.
/// </summary>
public sealed class WindowsPinnedSelfSignedReleaseAuthenticodeTests
{
    private const string Release = WindowsAuthenticodeVerifier.ReleaseSignerSubject;

    [Fact]
    public void ReleaseIdentityIsTheExactMsixPublisher()
    {
        Assert.Equal("CN=WaterRun, OU=\"Clash#\"", Release);
    }

    [Theory]
    [InlineData(-60, true)]
    [InlineData(0, false)]
    [InlineData(-96, false)]
    public void ReleaseChainUsesTheWindowsVerifiedTimeInsteadOfDownloadTime(int hoursFromNow, bool accepted)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = Create(Release, now.AddDays(-3), now.AddDays(-2));

        Assert.Equal(accepted, WindowsAuthenticodeVerifier.IsValidPinnedReleaseCertificate(certificate, now.AddHours(hoursFromNow).UtcDateTime));
    }

    [Theory]
    [InlineData("CN=WaterRun, OU=\"Clash#\"", true)]
    [InlineData("CN=WaterRun", false)]
    [InlineData("CN=waterrun, OU=\"Clash#\"", false)]
    [InlineData("CN=WaterRun, OU=\"Clash#\", O=Other", false)]
    [InlineData("OU=\"Clash#\", CN=WaterRun", false)]
    [InlineData("CN=Other Untrusted Publisher", false)]
    [InlineData("CN=ClashSharp Internal Candidate 0123456789abcdef0123456789abcdef", false)]
    public void OnlyTheExactReleaseSubjectIsAdmitted(string subject, bool accepted)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = Create(subject, now.AddDays(-1), now.AddDays(1));

        Assert.Equal(accepted, WindowsAuthenticodeVerifier.IsValidPinnedReleaseCertificate(certificate, now.UtcDateTime));
    }

    [Fact]
    public void AnInternalCandidateIdentityIsNotAReleaseIdentityAndTheReverse()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 internalCandidate = Create("CN=ClashSharp Internal Candidate " + Guid.NewGuid().ToString("N"), now.AddDays(-1), now.AddDays(1));
        using X509Certificate2 release = Create(Release, now.AddDays(-1), now.AddDays(1));

        Assert.True(WindowsAuthenticodeVerifier.IsValidInternalCertificate(internalCandidate, now.UtcDateTime));
        Assert.False(WindowsAuthenticodeVerifier.IsValidPinnedReleaseCertificate(internalCandidate, now.UtcDateTime));
        Assert.True(WindowsAuthenticodeVerifier.IsValidPinnedReleaseCertificate(release, now.UtcDateTime));
        Assert.False(WindowsAuthenticodeVerifier.IsValidInternalCertificate(release, now.UtcDateTime));
    }

    [Fact]
    public void ACertificateIssuedByAnotherAuthorityIsNotAReleaseIdentity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using RSA issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=Some Issuer", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 issuer = issuerRequest.CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
        using RSA leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(Release, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
        using X509Certificate2 leaf = leafRequest.Create(issuer, now.AddDays(-1), now.AddDays(1), Guid.NewGuid().ToByteArray());

        Assert.False(WindowsAuthenticodeVerifier.IsValidPinnedReleaseCertificate(leaf, now.UtcDateTime));
    }

    [Fact]
    public void ACertificateWithoutTheCodeSigningUsageIsNotAReleaseIdentity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(Release, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));

        Assert.False(WindowsAuthenticodeVerifier.IsValidPinnedReleaseCertificate(certificate, now.UtcDateTime));
    }

    [Fact]
    public async Task PinnedReleaseWorksWithoutInstallingItsRoot()
    {
        using var fixture = new UntrustedSignedFixture(Release);
        string[] rootsBefore = UntrustedSignedFixture.ReadRoots();

        using (IWindowsInstallerExecutableTrustLease lease = await fixture
            .Verifier(AuthenticodeUntrustedRootPolicy.PinnedSelfSignedRelease)
            .VerifyAsync(fixture.Path, CancellationToken.None))
        {
            Assert.Equal(fixture.Path, lease.ExecutablePath);
            Assert.Throws<IOException>(() => File.OpenWrite(fixture.Path).Dispose());
        }

        Assert.DoesNotContain(fixture.Thumbprint, rootsBefore);
        Assert.DoesNotContain(fixture.Thumbprint, UntrustedSignedFixture.ReadRoots());
    }

    [Theory]
    [InlineData(nameof(AuthenticodeUntrustedRootPolicy.SystemTrustOnly))]
    [InlineData(nameof(AuthenticodeUntrustedRootPolicy.PinnedInternalCandidate))]
    public async Task EveryOtherPolicyStillRefusesTheReleaseIdentityWithoutSystemTrust(string policyName)
    {
        using var fixture = new UntrustedSignedFixture(Release);
        var policy = Enum.Parse<AuthenticodeUntrustedRootPolicy>(policyName);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(policy).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }

    [Fact]
    public async Task ReleasePolicyDoesNotAdmitAnArbitrarySelfSignedPublisher()
    {
        using var fixture = new UntrustedSignedFixture("CN=Other Untrusted Publisher");

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(AuthenticodeUntrustedRootPolicy.PinnedSelfSignedRelease).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }

    [Fact]
    public async Task ReleasePolicyDoesNotAdmitAnInternalCandidateIdentity()
    {
        using var fixture = new UntrustedSignedFixture();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(AuthenticodeUntrustedRootPolicy.PinnedSelfSignedRelease).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }

    [Fact]
    public async Task ReleasePolicyRejectsAValidSignatureFromAnotherPinnedIdentity()
    {
        using var fixture = new UntrustedSignedFixture(Release);
        string different = (fixture.Thumbprint[0] == 'A' ? "B" : "A") + fixture.Thumbprint[1..];
        var verifier = new WindowsInstallerExecutableTrustVerifier(different, new WindowsAuthenticodeVerifier(AuthenticodeUntrustedRootPolicy.PinnedSelfSignedRelease));

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.signer_mismatch", failure.DiagnosticCode);
    }

    [Fact]
    public async Task ReleasePolicyCannotMaskModifiedExecutableContents()
    {
        using var fixture = new UntrustedSignedFixture(Release);
        fixture.ModifyCode();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(AuthenticodeUntrustedRootPolicy.PinnedSelfSignedRelease).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.authenticode_invalid", failure.DiagnosticCode);
    }

    [Fact]
    public void AnUndefinedPolicyIsRejectedInsteadOfBeingTreatedAsPermissive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsAuthenticodeVerifier((AuthenticodeUntrustedRootPolicy)99));
    }

    [Fact]
    public async Task AnOrdinaryBuildUsesTheStrictPolicyByDefault()
    {
        // This test project defines neither profile constant, so the verifier an installer gets by default is the strict one.
        using var fixture = new UntrustedSignedFixture(Release);
        var verifier = new WindowsInstallerExecutableTrustVerifier(fixture.Thumbprint);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }

    [Fact]
    public async Task StrictMeaningOfTheLegacyBooleanIsKept()
    {
        using var fixture = new UntrustedSignedFixture(Release);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(allowInternal: false).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }

    private static X509Certificate2 Create(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
