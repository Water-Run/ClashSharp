using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Certificates;

/// <summary>
/// Machine-wide ownership of one exact MSIX publisher certificate. This evidence is independent of
/// interactive account ownership and cannot authorize a CurrentUser or Root store operation.
/// </summary>
/// <param name="Schema">Strict document schema.</param>
/// <param name="LedgerId">Random identity distinguishing successive ownership lifetimes.</param>
/// <param name="PackageName">Product package name.</param>
/// <param name="Publisher">Exact package publisher distinguished name.</param>
/// <param name="PublisherId">Windows-derived publisher identifier.</param>
/// <param name="CertificateThumbprint">Uppercase SHA-1 certificate identifier.</param>
/// <param name="CertificateSha256">Lowercase SHA-256 of the full public DER certificate.</param>
/// <param name="InstallerOwned">Whether the installer recorded absence before importing.</param>
public sealed record InstallerMachineCertificateOwnership(
    int Schema,
    string LedgerId,
    string PackageName,
    string Publisher,
    string PublisherId,
    string CertificateThumbprint,
    string CertificateSha256,
    bool InstallerOwned)
{
    /// <summary>Gets the supported machine trust schema.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Gets the fixed private ownership filename.</summary>
    public const string FileName = "machine-certificate-ownership-v1.json";

    /// <summary>Gets the maximum canonical UTF-8 document size.</summary>
    public const int MaximumDocumentBytes = 16384;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 2,
    };

    /// <summary>Gets the only store location authorized by this document.</summary>
    public string StoreLocation => "localMachine";

    /// <summary>Gets the only certificate store authorized by this document.</summary>
    public string StoreName => "trustedPeople";

    /// <summary>Creates immutable ownership before importing a missing certificate.</summary>
    public static InstallerMachineCertificateOwnership Create(
        InstallerReleaseManifest manifest, bool certificateWasPresent)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        return new(CurrentSchema, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            manifest.PackageIdentity.Name, manifest.PackageIdentity.Publisher, manifest.PackageIdentity.PublisherId,
            manifest.PackageCertificateThumbprint, manifest.CertificateSha256, !certificateWasPresent);
    }

    /// <summary>Validates the exact product, publisher, certificate, and store-independent identity.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema)
        {
            throw new InstallerProtocolException("installer.machine_certificate.schema_invalid");
        }
        InstallerProtocolValidation.ValidateLowerHex256(LedgerId, "installer.machine_certificate.ledger_id_invalid");
        InstallerProtocolValidation.ValidateUpperHex160(CertificateThumbprint, "installer.machine_certificate.thumbprint_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(CertificateSha256, "installer.machine_certificate.sha256_invalid");
        InstallerPackageIdentityValidation.ValidateCommon(PackageName, Publisher, PublisherId, "1.0.0.0", "x64", string.Empty,
            $"{PackageName}_1.0.0.0_x64__{PublisherId}", $"{PackageName}_{PublisherId}", "installer.machine_certificate.package_invalid");
    }

    /// <summary>Rejects another product or certificate, including a different release signer.</summary>
    public void RequireManifest(InstallerReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate();
        manifest.Validate();
        if (PackageName != manifest.PackageIdentity.Name || Publisher != manifest.PackageIdentity.Publisher
            || PublisherId != manifest.PackageIdentity.PublisherId
            || CertificateThumbprint != manifest.PackageCertificateThumbprint || CertificateSha256 != manifest.CertificateSha256)
        {
            throw new InstallerProtocolException("installer.machine_certificate.ownership_conflict");
        }
    }

    /// <summary>Produces the only accepted representation for compare-and-swap recovery.</summary>
    public byte[] Serialize()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.machine_certificate.size_invalid");
        }
        return bytes;
    }

    /// <summary>Rejects missing, duplicate, unknown, wrongly typed, noncanonical, or altered store fields.</summary>
    public static InstallerMachineCertificateOwnership Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.machine_certificate.size_invalid");
        }
        try
        {
            InstallerMachineCertificateOwnership ownership = JsonSerializer.Deserialize<InstallerMachineCertificateOwnership>(bytes, Options)
                ?? throw new JsonException();
            byte[] canonical = ownership.Serialize();
            try
            {
                if (!bytes.SequenceEqual(canonical))
                {
                    throw new JsonException();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            return ownership;
        }
        catch (JsonException exception)
        {
            throw new InstallerProtocolException("installer.machine_certificate.json_invalid", exception);
        }
    }
}
