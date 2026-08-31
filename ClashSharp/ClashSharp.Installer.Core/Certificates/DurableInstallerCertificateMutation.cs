using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Certificates;

/// <summary>
/// Applies certificate changes through a write-ahead ownership ledger so retries are idempotent
/// and certificates that predate ClashSharp are never removed.
/// </summary>
public sealed class DurableInstallerCertificateMutation :
    IInstallerCertificateMutation,
    IInstallerCertificateMutationVerifier
{
    private readonly IInstallerCertificateOwnershipStore _ownershipStore;
    private readonly IInstallerCertificateStoreAdapter _certificateStore;

    /// <summary>Initializes the durable certificate mutation capability.</summary>
    public DurableInstallerCertificateMutation(
        IInstallerCertificateOwnershipStore ownershipStore,
        IInstallerCertificateStoreAdapter certificateStore)
    {
        ArgumentNullException.ThrowIfNull(ownershipStore);
        ArgumentNullException.ThrowIfNull(certificateStore);
        _ownershipStore = ownershipStore;
        _certificateStore = certificateStore;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Release.Validate();
        if (request.Operation == InstallerOperation.Uninstall)
        {
            await RemoveIfOwnedAsync(request, release, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EnsureInstalledAsync(request, release, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task VerifyAppliedAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Release.Validate();
        InstallerCertificateOwnershipSnapshot? ownership = await _ownershipStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        InstallerCertificatePresence presence = await InspectExactAsync(
                request,
                release,
                cancellationToken)
            .ConfigureAwait(false);

        if (request.Operation is InstallerOperation.Install or InstallerOperation.Repair)
        {
            if (ownership is null)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.ownership_missing");
            }

            ValidateActiveOwnership(ownership.Ledger, request, release);
            if (presence != InstallerCertificatePresence.ExactMatch)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.installation_verification_failed");
            }

            return;
        }

        if (ownership is not null)
        {
            ValidateMatchingOwnership(ownership.Ledger, request, release);
            throw new InstallerProtocolException(
                "installer.certificate.removal_incomplete");
        }

        if (presence is not (
                InstallerCertificatePresence.Missing
                or InstallerCertificatePresence.ExactMatch))
        {
            throw new InstallerProtocolException(
                "installer.certificate.removal_verification_failed");
        }
    }

    private async Task EnsureInstalledAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        InstallerCertificateOwnershipSnapshot? ownership = await _ownershipStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (ownership is null)
        {
            InstallerCertificatePresence initialPresence = await InspectExactAsync(
                    request,
                    release,
                    cancellationToken)
                .ConfigureAwait(false);
            InstallerCertificateOwnershipLedger created = InstallerCertificateOwnershipLedger.Create(
                request,
                release.Release,
                certificateWasPresent: initialPresence == InstallerCertificatePresence.ExactMatch);
            ownership = await _ownershipStore
                .SaveAsync(created, expectedCurrentHash: null, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            ValidateActiveOwnership(ownership.Ledger, request, release);
        }

        InstallerCertificatePresence presence = await InspectExactAsync(
                request,
                release,
                cancellationToken)
            .ConfigureAwait(false);
        if (presence == InstallerCertificatePresence.ExactMatch)
        {
            return;
        }

        if (!release.Release.CertificatePayloadAvailable)
        {
            throw new InstallerProtocolException("installer.release.certificate_payload_missing");
        }

        if (ownership.Ledger.WasPreExisting)
        {
            InstallerCertificateOwnershipLedger owned = ownership.Ledger
                .TakeOwnershipForMissingCertificate();
            ownership = await _ownershipStore
                .SaveAsync(owned, ownership.ContentHash, cancellationToken)
                .ConfigureAwait(false);
        }

        await _certificateStore.ImportAsync(request, release, cancellationToken).ConfigureAwait(false);
        if (await InspectExactAsync(request, release, cancellationToken).ConfigureAwait(false)
            != InstallerCertificatePresence.ExactMatch)
        {
            throw new InstallerProtocolException("installer.certificate.import_verification_failed");
        }
    }

    private async Task RemoveIfOwnedAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        InstallerCertificateOwnershipSnapshot? ownership = await _ownershipStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (ownership is null)
        {
            return;
        }

        ValidateMatchingOwnership(ownership.Ledger, request, release);
        if (ownership.Ledger.ManagedReferenceCount == 1)
        {
            InstallerCertificateOwnershipLedger unreferenced = ownership.Ledger.PrepareRemoval();
            ownership = await _ownershipStore
                .SaveAsync(unreferenced, ownership.ContentHash, cancellationToken)
                .ConfigureAwait(false);
        }

        if (ownership.Ledger.InstallerOwned)
        {
            InstallerCertificatePresence presence = await InspectExactAsync(
                    request,
                    release,
                    cancellationToken)
                .ConfigureAwait(false);
            if (presence == InstallerCertificatePresence.ExactMatch)
            {
                await _certificateStore
                    .RemoveExactAsync(request, release, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (await InspectExactAsync(request, release, cancellationToken).ConfigureAwait(false)
                != InstallerCertificatePresence.Missing)
            {
                throw new InstallerProtocolException("installer.certificate.removal_verification_failed");
            }
        }

        await _ownershipStore.ClearUnreferencedAsync(
                ownership.Ledger.LedgerId,
                ownership.ContentHash,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<InstallerCertificatePresence> InspectExactAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        InstallerCertificatePresence presence = await _certificateStore
            .InspectAsync(request, release, cancellationToken)
            .ConfigureAwait(false);
        if (presence == InstallerCertificatePresence.IdentityConflict
            || !Enum.IsDefined(presence))
        {
            throw new InstallerProtocolException("installer.certificate.identity_conflict");
        }

        return presence;
    }

    private static void ValidateActiveOwnership(
        InstallerCertificateOwnershipLedger ledger,
        InstallerRequest request,
        IInstallerReleaseLease release)
    {
        ValidateMatchingOwnership(ledger, request, release);
        if (ledger.ManagedReferenceCount != 1)
        {
            throw new InstallerProtocolException("installer.certificate.ownership_conflict");
        }
    }

    private static void ValidateMatchingOwnership(
        InstallerCertificateOwnershipLedger ledger,
        InstallerRequest request,
        IInstallerReleaseLease release)
    {
        if (!ledger.Matches(request, release.Release))
        {
            throw new InstallerProtocolException("installer.certificate.ownership_conflict");
        }
    }
}
