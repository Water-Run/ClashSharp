using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Removes only the archived target user's exact TrustedPeople identity. The account is fixed by
/// helper authentication; an alternate administrator's CurrentUser store is never selected.
/// Unlike release installation, this capability accepts no payload and cannot import trust.
/// </summary>
internal sealed class WindowsArchivedCertificateRemovalAdapter : IInstallerArchivedCertificateRemovalAdapter
{
    private readonly string _targetSid;
    private readonly IWindowsCertificateStoreNative _native;

    internal WindowsArchivedCertificateRemovalAdapter(string authenticatedTargetSid)
        : this(authenticatedTargetSid, WindowsTargetUserCertificateStoreNative.Instance)
    {
    }

    internal WindowsArchivedCertificateRemovalAdapter(string authenticatedTargetSid,
        IWindowsCertificateStoreNative native)
    {
        InstallerProtocolValidation.ValidateTargetSid(authenticatedTargetSid);
        ArgumentNullException.ThrowIfNull(native);
        _targetSid = authenticatedTargetSid;
        _native = native;
    }

    public Task<InstallerCertificatePresence> InspectAsync(
        InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        Validate(ledger, cancellationToken);
        try
        {
            using IWindowsCertificateStore? store = _native.Open(_targetSid, writable: false, createIfMissing: false);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(store is null ? InstallerCertificatePresence.Missing
                : WindowsCertificateIdentity.InspectStore(store, ledger.CertificateThumbprint, ledger.CertificateSha256, cancellationToken));
        }
        catch (Exception exception) when (IsUnexpectedRecoverable(exception))
        {
            throw new InstallerProtocolException("installer.certificate_archive.inspection_failed");
        }
    }

    public Task RemoveExactAsync(InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        Validate(ledger, cancellationToken);
        if (!ledger.InstallerOwned || ledger.ManagedReferenceCount != 0)
        {
            throw new InstallerProtocolException("installer.certificate_archive.removal_not_authorized");
        }
        try
        {
            using IWindowsCertificateStore? store = _native.Open(_targetSid, writable: true, createIfMissing: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (store is null)
            {
                return Task.CompletedTask;
            }
            InstallerCertificatePresence presence = Inspect(store, ledger, cancellationToken);
            if (presence == InstallerCertificatePresence.Missing)
            {
                return Task.CompletedTask;
            }
            cancellationToken.ThrowIfCancellationRequested();
            _ = store.DeleteExactCertificates(ledger.CertificateThumbprint, ledger.CertificateSha256, cancellationToken);
            if (Inspect(store, ledger, cancellationToken) != InstallerCertificatePresence.Missing)
            {
                throw new InstallerProtocolException("installer.certificate_archive.removal_verification_failed");
            }
            return Task.CompletedTask;
        }
        catch (Exception exception) when (IsUnexpectedRecoverable(exception))
        {
            // Native errors can contain account-store names. Only the stable code leaves this boundary.
            throw new InstallerProtocolException("installer.certificate_archive.removal_failed");
        }
    }

    private void Validate(InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ledger.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (ledger.TargetSid != _targetSid)
        {
            throw new InstallerProtocolException("installer.certificate_archive.target_sid_mismatch");
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || !Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
        {
            throw new InstallerProtocolException("installer.certificate.platform_unsupported");
        }
    }

    private static InstallerCertificatePresence Inspect(IWindowsCertificateStore store,
        InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        InstallerCertificatePresence presence = WindowsCertificateIdentity.InspectStore(
            store, ledger.CertificateThumbprint, ledger.CertificateSha256, cancellationToken);
        if (presence == InstallerCertificatePresence.IdentityConflict)
        {
            throw new InstallerProtocolException("installer.certificate_archive.identity_conflict");
        }
        return presence;
    }

    private static bool IsUnexpectedRecoverable(Exception exception) => exception is not (
        InstallerProtocolException or OperationCanceledException or OutOfMemoryException
        or StackOverflowException or AccessViolationException or AppDomainUnloadedException);
}
