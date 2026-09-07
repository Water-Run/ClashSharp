using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Certificates;

/// <summary>Exposes only exact inspection and deletion of previously owned target-user trust.</summary>
public interface IInstallerArchivedCertificateRemovalAdapter
{
    /// <summary>Inspects the archive's exact target SID, thumbprint and full DER SHA-256.</summary>
    Task<InstallerCertificatePresence> InspectAsync(
        InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken);

    /// <summary>Deletes only an installer-owned identity whose reference is already durably zero.</summary>
    Task RemoveExactAsync(InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken);
}

/// <summary>
/// Revalidates the dedicated helper's retained exclusion and candidate, the authenticated retired
/// account, absence of its package, and the shared installation's different current owner.
/// It grants no shared-service, association, package or certificate mutation capability.
/// </summary>
public interface IInstallerArchivedCertificateRemovalBoundary
{
    /// <summary>Rejects stale authority or any changed removal precondition before further work.</summary>
    Task ReverifyAsync(string authenticatedTargetSid, CancellationToken cancellationToken);
}

/// <summary>
/// Releases a retired account's archived trust only after independent package-removal verification.
/// The archived identity can differ from the current candidate's signing certificate. This object
/// has neither an import capability nor access to shared machine mutations.
/// </summary>
public sealed class InstallerArchivedCertificateRemoval
{
    private readonly string _targetSid;
    private readonly IInstallerArchivedCertificateStore _store;
    private readonly IInstallerArchivedCertificateRemovalAdapter _certificates;
    private readonly IInstallerArchivedCertificateRemovalBoundary _boundary;
    private int _active;

    /// <summary>Initializes the removal capability without reading state or acquiring authority.</summary>
    /// <param name="targetSid">Canonical retired account SID obtained from parent authentication.</param>
    /// <param name="store">Private archive store bound to that same account.</param>
    /// <param name="certificates">Exact inspection and removal adapter without import authority.</param>
    /// <param name="boundary">Dedicated helper authority and package-absence verifier.</param>
    public InstallerArchivedCertificateRemoval(string targetSid, IInstallerArchivedCertificateStore store,
        IInstallerArchivedCertificateRemovalAdapter certificates, IInstallerArchivedCertificateRemovalBoundary boundary)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(boundary);
        _targetSid = targetSid;
        _store = store;
        _certificates = certificates;
        _boundary = boundary;
    }

    /// <summary>
    /// Releases the reference before optional exact deletion, then clears only verified evidence.
    /// Failed or cancelled certificate operations retain the unreferenced archive for safe replay.
    /// </summary>
    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            await ReverifyAsync(cancellationToken).ConfigureAwait(false);
            InstallerCertificateOwnershipSnapshot? current = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                // Absence is not ownership: there is no certificate identity we may delete.
                await ReverifyAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            InstallerArchivedCertificateStore.ValidateSnapshot(current, _targetSid);
            if (current.Ledger.InstallerOwned)
            {
                _ = await InspectAsync(current.Ledger, cancellationToken).ConfigureAwait(false);
            }
            await ReverifyAsync(cancellationToken).ConfigureAwait(false);
            if (current.Ledger.ManagedReferenceCount == 1)
            {
                InstallerCertificateOwnershipLedger expected = current.Ledger.PrepareRemoval();
                current = await _store.ReleaseReferenceAsync(current, cancellationToken).ConfigureAwait(false);
                InstallerArchivedCertificateStore.ValidateSnapshot(current, _targetSid);
                if (current.Ledger != expected)
                {
                    throw new InstallerProtocolException("installer.certificate_archive.release_mismatch");
                }
            }

            await ReverifyAsync(cancellationToken).ConfigureAwait(false);
            if (current.Ledger.InstallerOwned)
            {
                if (await InspectAsync(current.Ledger, cancellationToken).ConfigureAwait(false)
                    == InstallerCertificatePresence.ExactMatch)
                {
                    await ReverifyAsync(cancellationToken).ConfigureAwait(false);
                    await _certificates.RemoveExactAsync(current.Ledger, cancellationToken).ConfigureAwait(false);
                }
                if (await InspectAsync(current.Ledger, cancellationToken).ConfigureAwait(false)
                    != InstallerCertificatePresence.Missing)
                {
                    throw new InstallerProtocolException("installer.certificate_archive.removal_verification_failed");
                }
            }

            // Pre-existing trust is preserved without ever opening a writable certificate store.
            await ReverifyAsync(cancellationToken).ConfigureAwait(false);
            await _store.ClearAsync(current, cancellationToken).ConfigureAwait(false);
            await VerifyCompletedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    /// <summary>Verifies both private archive absence and the still-valid removal boundary.</summary>
    public async Task VerifyCompletedAsync(CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            await VerifyCompletedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    private async Task VerifyCompletedCoreAsync(CancellationToken cancellationToken)
    {
        await ReverifyAsync(cancellationToken).ConfigureAwait(false);
        if (await _store.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InstallerProtocolException("installer.certificate_archive.removal_incomplete");
        }
        await ReverifyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReverifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _boundary.ReverifyAsync(_targetSid, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<InstallerCertificatePresence> InspectAsync(
        InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        InstallerCertificatePresence presence = await _certificates.InspectAsync(ledger, cancellationToken).ConfigureAwait(false);
        if (presence is not (InstallerCertificatePresence.Missing or InstallerCertificatePresence.ExactMatch))
        {
            throw new InstallerProtocolException("installer.certificate_archive.identity_conflict");
        }
        return presence;
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InstallerProtocolException("installer.certificate_archive.concurrent_removal");
        }
    }
}
