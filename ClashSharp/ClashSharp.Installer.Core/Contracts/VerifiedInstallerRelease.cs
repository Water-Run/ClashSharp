namespace ClashSharp.Installer.Contracts;

/// <summary>Represents an exact installer release verified from an immutable payload handle.</summary>
/// <param name="ExpectedPackageVersion">Canonical package version proven by the release manifest.</param>
/// <param name="InstallerPayloadSha256">Lowercase SHA-256 bound into the release manifest.</param>
/// <param name="PackagePayloadAvailable">Whether a verified package payload is available for deployment.</param>
/// <param name="PackageCertificateThumbprint">Uppercase SHA-1 identity of the verified MSIX certificate.</param>
/// <param name="CertificateSha256">Lowercase SHA-256 of the complete verified DER certificate.</param>
/// <param name="CertificatePayloadAvailable">Whether the verified certificate bytes are available to import.</param>
public sealed record VerifiedInstallerRelease(
    string ExpectedPackageVersion,
    string InstallerPayloadSha256,
    bool PackagePayloadAvailable,
    string PackageCertificateThumbprint,
    string CertificateSha256,
    bool CertificatePayloadAvailable)
{
    /// <summary>Validates the verified release value.</summary>
    public void Validate()
    {
        InstallerProtocolValidation.ValidatePackageVersion(ExpectedPackageVersion);
        InstallerProtocolValidation.ValidateLowerHex256(
            InstallerPayloadSha256,
            "installer.release.payload_hash_invalid");
        InstallerProtocolValidation.ValidateUpperHex160(
            PackageCertificateThumbprint,
            "installer.release.certificate_thumbprint_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(
            CertificateSha256,
            "installer.release.certificate_hash_invalid");
    }
}
