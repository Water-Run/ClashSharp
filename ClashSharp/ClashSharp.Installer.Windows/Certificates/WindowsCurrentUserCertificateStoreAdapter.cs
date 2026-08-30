using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Applies exact certificate operations only when the invoking token is the target user.
/// This parent-side adapter deliberately rejects an OTS helper running as another administrator;
/// helper composition requires an exact-target-SID store adapter instead.
/// </summary>
public sealed class WindowsCurrentUserCertificateStoreAdapter : IInstallerCertificateStoreAdapter
{
    /// <inheritdoc />
    public Task<InstallerCertificatePresence> InspectAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return Task.FromResult(InspectCollection(store.Certificates, release.Release));
    }

    /// <inheritdoc />
    public Task ImportAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        WindowsInstallerReleaseLease windowsLease = ValidateBoundary(
            request,
            release,
            cancellationToken);
        WindowsLockedPayloadFile certificateFile = windowsLease.RequireFile(
            InstallerPayloadFileRole.Certificate);
        byte[] bytes = certificateFile.ReadAllBytes(cancellationToken);
        try
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(bytes);
            ValidateExactCertificate(certificate, release.Release);
            if (certificate.HasPrivateKey)
            {
                throw new InstallerProtocolException("installer.certificate.private_key_rejected");
            }

            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
            cancellationToken.ThrowIfCancellationRequested();
            store.Add(certificate);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public Task RemoveExactAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        X509Certificate2Collection certificates = store.Certificates;
        try
        {
            var exact = new List<X509Certificate2>();
            bool conflict = false;
            foreach (X509Certificate2 certificate in certificates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ThumbprintMatches(certificate, release.Release))
                {
                    continue;
                }

                if (DerHashMatches(certificate, release.Release))
                {
                    exact.Add(certificate);
                }
                else
                {
                    conflict = true;
                }
            }

            if (conflict)
            {
                throw new InstallerProtocolException("installer.certificate.identity_conflict");
            }

            foreach (X509Certificate2 certificate in exact)
            {
                cancellationToken.ThrowIfCancellationRequested();
                store.Remove(certificate);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        finally
        {
            DisposeCertificates(certificates);
        }
    }

    private static WindowsInstallerReleaseLease ValidateBoundary(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Release.Validate();
        release.Manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (release is not WindowsInstallerReleaseLease windowsLease
            || !release.Manifest.Matches(release.Release))
        {
            throw new InstallerProtocolException("installer.release.windows_lease_required");
        }

        windowsLease.RequireRequest(request);

        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (!string.Equals(identity.User?.Value, request.TargetSid, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.certificate.target_user_mismatch");
        }

        return windowsLease;
    }

    private static InstallerCertificatePresence InspectCollection(
        X509Certificate2Collection certificates,
        VerifiedInstallerRelease release)
    {
        bool exact = false;
        bool conflict = false;
        try
        {
            foreach (X509Certificate2 certificate in certificates)
            {
                if (!ThumbprintMatches(certificate, release))
                {
                    continue;
                }

                if (DerHashMatches(certificate, release))
                {
                    exact = true;
                }
                else
                {
                    conflict = true;
                }
            }

            return conflict
                ? InstallerCertificatePresence.IdentityConflict
                : exact
                    ? InstallerCertificatePresence.ExactMatch
                    : InstallerCertificatePresence.Missing;
        }
        finally
        {
            DisposeCertificates(certificates);
        }
    }

    private static void ValidateExactCertificate(
        X509Certificate2 certificate,
        VerifiedInstallerRelease release)
    {
        if (!ThumbprintMatches(certificate, release) || !DerHashMatches(certificate, release))
        {
            throw new InstallerProtocolException("installer.certificate.payload_identity_invalid");
        }
    }

    private static bool ThumbprintMatches(
        X509Certificate2 certificate,
        VerifiedInstallerRelease release) =>
        string.Equals(
            Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA1)),
            release.PackageCertificateThumbprint,
            StringComparison.Ordinal);

    private static bool DerHashMatches(
        X509Certificate2 certificate,
        VerifiedInstallerRelease release) =>
        string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)),
            release.CertificateSha256,
            StringComparison.Ordinal);

    private static void DisposeCertificates(X509Certificate2Collection certificates)
    {
        foreach (X509Certificate2 certificate in certificates)
        {
            certificate.Dispose();
        }
    }
}
