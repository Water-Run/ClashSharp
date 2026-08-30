using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerProtocolValidationTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("1.2.3.4")]
    [InlineData("65535.65535.65535.65535")]
    public void CanonicalPackageVersionsAreAccepted(string value) =>
        InstallerProtocolValidation.ValidatePackageVersion(value);

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("01.2.3.4")]
    [InlineData("1..3.4")]
    [InlineData("65536.0.0.0")]
    [InlineData("1.2.-3.4")]
    [InlineData("1.2. 3.4")]
    public void NoncanonicalPackageVersionsAreRejected(string value)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
            () => InstallerProtocolValidation.ValidatePackageVersion(value));
        Assert.Equal("installer.request.package_version_invalid", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("S-1-5-21-100-200-300-1001")]
    [InlineData("S-1-5-21-4294967295")]
    public void CanonicalSidTextIsAccepted(string value) =>
        InstallerProtocolValidation.ValidateTargetSid(value);

    [Theory]
    [InlineData("")]
    [InlineData("s-1-5-21")]
    [InlineData("S-1")]
    [InlineData("S-1-5-name")]
    [InlineData("S-1-5-")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-01-5-21-1001")]
    [InlineData("S-1-05-21-1001")]
    [InlineData("S-1-5-21-4294967296")]
    [InlineData("S-2-5-21-1001")]
    public void NoncanonicalSidTextIsRejected(string value) =>
        Assert.Throws<InstallerProtocolException>(
            () => InstallerProtocolValidation.ValidateTargetSid(value));

    [Fact]
    public void HashRequiresExactLowercaseSha256()
    {
        InstallerProtocolValidation.ValidateLowerHex256(InstallerTestData.Hash, "invalid");
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerProtocolValidation.ValidateLowerHex256(
                InstallerTestData.Hash.ToUpperInvariant(),
                "invalid"));
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerProtocolValidation.ValidateLowerHex256(InstallerTestData.Hash[..63], "invalid"));
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerProtocolValidation.ValidateLowerHex256(
                InstallerTestData.Hash[..63] + "g",
                "invalid"));
    }

    [Fact]
    public void ThumbprintRequiresExactUppercaseSha1()
    {
        InstallerProtocolValidation.ValidateUpperHex160(
            InstallerTestData.CertificateThumbprint,
            "invalid");
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerProtocolValidation.ValidateUpperHex160(
                InstallerTestData.CertificateThumbprint.ToLowerInvariant(),
                "invalid"));
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerProtocolValidation.ValidateUpperHex160(
                InstallerTestData.CertificateThumbprint[..39],
                "invalid"));
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerProtocolValidation.ValidateUpperHex160(
                InstallerTestData.CertificateThumbprint[..39] + "G",
                "invalid"));
    }

    [Theory]
    [InlineData("installer.completed")]
    [InlineData("installer.machine_helper.result_invalid")]
    [InlineData("a.b")]
    public void StableDiagnosticCodesAreAccepted(string value) =>
        InstallerProtocolValidation.ValidateDiagnosticCode(value);

    [Theory]
    [InlineData("")]
    [InlineData(".installer")]
    [InlineData("installer.")]
    [InlineData("installer..failed")]
    [InlineData("Installer.failed")]
    [InlineData("installer-failed")]
    [InlineData("installer failed")]
    [InlineData("installer\nfailed")]
    public void UnsafeDiagnosticCodesAreRejected(string value)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerProtocolValidation.ValidateDiagnosticCode(value));
        Assert.Equal("installer.diagnostic_code_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void ReassociationIsOnlyLegalForExplicitRepair()
    {
        InstallerTestData.Request(InstallerOperation.Repair, allowReassociation: true).Validate();
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
            () => InstallerTestData.Request(allowReassociation: true).Validate());
        Assert.Equal("installer.request.reassociation_invalid", exception.DiagnosticCode);
    }
}
