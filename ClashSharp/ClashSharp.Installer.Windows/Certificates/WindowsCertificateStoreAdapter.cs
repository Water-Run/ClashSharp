using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Shares exact DER identity, locked payload, idempotent import, and conflict-rejecting removal
/// between explicitly bound certificate-store adapters. The native factory fixes the store scope.
/// </summary>
internal abstract class WindowsCertificateStoreAdapter : IInstallerCertificateStoreAdapter
{
    private readonly IWindowsCertificateStoreNative _native;

    protected WindowsCertificateStoreAdapter(IWindowsCertificateStoreNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public Task<InstallerCertificatePresence> InspectAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        try
        {
            using IWindowsCertificateStore? store = _native.Open(
                request.TargetSid,
                writable: false,
                createIfMissing: false);
            if (store is null)
            {
                return Task.FromResult(InstallerCertificatePresence.Missing);
            }

            return Task.FromResult(InspectStore(store, release.Release, cancellationToken));
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.certificate.inspection_failed",
                exception);
        }
    }

    public virtual Task ImportAsync(
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
                throw new InstallerProtocolException(
                    "installer.certificate.private_key_rejected");
            }

            using IWindowsCertificateStore store = _native.Open(
                request.TargetSid,
                writable: true,
                createIfMissing: true)
                ?? throw new InstallerProtocolException(
                    "installer.certificate.store_creation_failed");
            InstallerCertificatePresence presence = InspectStore(
                store,
                release.Release,
                cancellationToken);
            ThrowIfConflict(presence);
            if (presence == InstallerCertificatePresence.ExactMatch)
            {
                return Task.CompletedTask;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                store.AddEncodedCertificate(bytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // CERT_STORE_ADD_NEW can lose a benign race to another exact importer. Re-read
                // through the same fixed store before classifying the result as a failure.
                InstallerCertificatePresence racedPresence = InspectStore(
                    store,
                    release.Release,
                    cancellationToken);
                ThrowIfConflict(racedPresence);
                if (racedPresence != InstallerCertificatePresence.ExactMatch)
                {
                    throw new InstallerProtocolException(
                        "installer.certificate.import_failed",
                        exception);
                }

                return Task.CompletedTask;
            }

            if (InspectStore(store, release.Release, cancellationToken)
                != InstallerCertificatePresence.ExactMatch)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.import_verification_failed");
            }

            return Task.CompletedTask;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.certificate.import_failed",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public virtual Task RemoveExactAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release, cancellationToken);
        try
        {
            using IWindowsCertificateStore? store = _native.Open(
                request.TargetSid,
                writable: true,
                createIfMissing: false);
            if (store is null)
            {
                return Task.CompletedTask;
            }

            InstallerCertificatePresence presence = InspectStore(
                store,
                release.Release,
                cancellationToken);
            ThrowIfConflict(presence);
            if (presence == InstallerCertificatePresence.Missing)
            {
                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            store.DeleteExactCertificates(
                release.Release.PackageCertificateThumbprint,
                release.Release.CertificateSha256,
                cancellationToken);
            InstallerCertificatePresence remaining = InspectStore(
                store,
                release.Release,
                cancellationToken);
            ThrowIfConflict(remaining);
            if (remaining != InstallerCertificatePresence.Missing)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.removal_verification_failed");
            }

            return Task.CompletedTask;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.certificate.removal_failed",
                exception);
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            throw new InstallerProtocolException(
                "installer.certificate.platform_unsupported");
        }

        if (release is not WindowsInstallerReleaseLease windowsLease
            || !release.Manifest.Matches(release.Release))
        {
            throw new InstallerProtocolException(
                "installer.release.windows_lease_required");
        }

        windowsLease.RequireRequest(request);
        return windowsLease;
    }

    private static InstallerCertificatePresence InspectStore(
        IWindowsCertificateStore store,
        VerifiedInstallerRelease release,
        CancellationToken cancellationToken) =>
        WindowsCertificateIdentity.InspectStore(store, release.PackageCertificateThumbprint,
            release.CertificateSha256, cancellationToken);

    private static void ValidateExactCertificate(
        X509Certificate2 certificate,
        VerifiedInstallerRelease release)
    {
        WindowsCertificateIdentity identity = WindowsCertificateIdentity.FromEncoded(
            certificate.RawData);
        if (!identity.Matches(
                release.PackageCertificateThumbprint,
                release.CertificateSha256))
        {
            throw new InstallerProtocolException(
                "installer.certificate.payload_identity_invalid");
        }
    }

    private static void ThrowIfConflict(InstallerCertificatePresence presence)
    {
        if (presence == InstallerCertificatePresence.IdentityConflict)
        {
            throw new InstallerProtocolException(
                "installer.certificate.identity_conflict");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}
