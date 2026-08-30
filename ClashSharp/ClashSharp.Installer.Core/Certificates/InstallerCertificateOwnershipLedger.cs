using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Certificates;

/// <summary>Restricts certificate ownership records to the target user's certificate store.</summary>
public enum InstallerCertificateStoreLocation
{
    /// <summary>The interactive target user's certificate store.</summary>
    CurrentUser,
}

/// <summary>Restricts certificate ownership records to the MSIX trust store.</summary>
public enum InstallerCertificateStoreName
{
    /// <summary>The Windows Trusted People store used for a private MSIX publisher.</summary>
    TrustedPeople,
}

/// <summary>
/// Records why one exact package-signing certificate may or may not be removed.
/// The record is written before importing or deleting a certificate and is therefore resumable.
/// </summary>
public sealed record InstallerCertificateOwnershipLedger(
    int Schema,
    string LedgerId,
    string TargetSid,
    string CertificateThumbprint,
    string CertificateSha256,
    InstallerCertificateStoreLocation StoreLocation,
    InstallerCertificateStoreName StoreName,
    bool WasPreExisting,
    bool InstallerOwned,
    int ManagedReferenceCount,
    int Generation)
{
    /// <summary>Gets the current strict ownership-ledger schema.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Creates write-ahead ownership state for one install or repair.</summary>
    /// <param name="request">Exact validated install or repair request.</param>
    /// <param name="release">Release containing the exact certificate identity.</param>
    /// <param name="certificateWasPresent">Whether the exact certificate existed before mutation.</param>
    /// <returns>A first-generation ledger with one managed product reference.</returns>
    public static InstallerCertificateOwnershipLedger Create(
        InstallerRequest request,
        VerifiedInstallerRelease release,
        bool certificateWasPresent)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Validate();
        if (request.Operation == InstallerOperation.Uninstall)
        {
            throw new InstallerProtocolException("installer.certificate.create_for_uninstall_invalid");
        }

        if (!certificateWasPresent && !release.CertificatePayloadAvailable)
        {
            throw new InstallerProtocolException("installer.release.certificate_payload_missing");
        }

        InstallerCertificateOwnershipLedger ledger = new(
            CurrentSchema,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            request.TargetSid,
            release.PackageCertificateThumbprint,
            release.CertificateSha256,
            InstallerCertificateStoreLocation.CurrentUser,
            InstallerCertificateStoreName.TrustedPeople,
            WasPreExisting: certificateWasPresent,
            InstallerOwned: !certificateWasPresent,
            ManagedReferenceCount: 1,
            Generation: 1);
        ledger.Validate();
        return ledger;
    }

    /// <summary>Checks that a request and verified release may resume this exact ledger.</summary>
    public bool Matches(InstallerRequest request, VerifiedInstallerRelease release)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Validate();
        Validate();
        return string.Equals(TargetSid, request.TargetSid, StringComparison.Ordinal)
            && string.Equals(
                CertificateThumbprint,
                release.PackageCertificateThumbprint,
                StringComparison.Ordinal)
            && string.Equals(CertificateSha256, release.CertificateSha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// Takes write-ahead ownership when a formerly pre-existing certificate has disappeared and
    /// repair must import the trusted release certificate again.
    /// </summary>
    public InstallerCertificateOwnershipLedger TakeOwnershipForMissingCertificate()
    {
        Validate();
        if (InstallerOwned)
        {
            return this;
        }

        if (!WasPreExisting || ManagedReferenceCount != 1 || Generation != 1)
        {
            throw new InstallerProtocolException("installer.certificate.ownership_transition_invalid");
        }

        InstallerCertificateOwnershipLedger advanced = this with
        {
            WasPreExisting = false,
            InstallerOwned = true,
            Generation = 2,
        };
        advanced.Validate();
        return advanced;
    }

    /// <summary>Durably releases the only managed reference before optional exact removal.</summary>
    public InstallerCertificateOwnershipLedger PrepareRemoval()
    {
        Validate();
        if (ManagedReferenceCount == 0)
        {
            return this;
        }

        InstallerCertificateOwnershipLedger advanced = this with
        {
            ManagedReferenceCount = 0,
            Generation = Generation + 1,
        };
        advanced.Validate();
        return advanced;
    }

    /// <summary>Validates exact store, identity, ownership, reference, and generation invariants.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema)
        {
            throw new InstallerProtocolException("installer.certificate.ledger_schema_invalid");
        }

        InstallerProtocolValidation.ValidateLowerHex256(
            LedgerId,
            "installer.certificate.ledger_id_invalid");
        InstallerProtocolValidation.ValidateTargetSid(TargetSid);
        InstallerProtocolValidation.ValidateUpperHex160(
            CertificateThumbprint,
            "installer.certificate.thumbprint_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(
            CertificateSha256,
            "installer.certificate.sha256_invalid");
        if (StoreLocation != InstallerCertificateStoreLocation.CurrentUser
            || StoreName != InstallerCertificateStoreName.TrustedPeople)
        {
            throw new InstallerProtocolException("installer.certificate.store_invalid");
        }

        if (WasPreExisting == InstallerOwned || ManagedReferenceCount is < 0 or > 1)
        {
            throw new InstallerProtocolException("installer.certificate.ownership_invalid");
        }

        bool generationValid = (Generation, ManagedReferenceCount, WasPreExisting, InstallerOwned) switch
        {
            (1, 1, true, false) or (1, 1, false, true) => true,
            (2, 1, false, true) => true,
            (2, 0, _, _) => true,
            (3, 0, false, true) => true,
            _ => false,
        };
        if (!generationValid)
        {
            throw new InstallerProtocolException("installer.certificate.generation_invalid");
        }
    }
}
