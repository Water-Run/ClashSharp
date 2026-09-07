using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Certificates;

/// <summary>
/// Owns machine MSIX trust separately from account-specific trust. Exact pre-existing certificates
/// are preserved; owned certificates remain recorded while any package still references the publisher.
/// Writes and certificate mutations reconcile their postconditions before returning from failures.
/// </summary>
public sealed class DurableInstallerMachineCertificateMutation : IInstallerCertificateMutation, IInstallerCertificateMutationVerifier
{
    private readonly IInstallerMachineCertificatePersistence _persistence;
    private readonly IInstallerCertificateStoreAdapter _certificates;
    private readonly IInstallerMachineCertificateReferences _references;
    private int _active;

    /// <summary>Creates a lazy capability owned by one authenticated elevated helper session.</summary>
    public DurableInstallerMachineCertificateMutation(IInstallerMachineCertificatePersistence persistence,
        IInstallerCertificateStoreAdapter certificates, IInstallerMachineCertificateReferences references)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(references);
        _persistence = persistence;
        _certificates = certificates;
        _references = references;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            await ReverifyAsync(request, release, cancellationToken).ConfigureAwait(false);
            InstallerMachineCertificateOwnership? ownership = await ReadAsync(release, cancellationToken).ConfigureAwait(false);
            InstallerCertificatePresence presence = await InspectAsync(request, release, cancellationToken).ConfigureAwait(false);
            if (request.Operation == InstallerOperation.Uninstall)
            {
                await RemoveAsync(request, release, ownership, presence, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await InstallAsync(request, release, ownership, presence, cancellationToken).ConfigureAwait(false);
            }
            await ReverifyAsync(request, release, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    /// <inheritdoc />
    public async Task VerifyAppliedAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            await ReverifyAsync(request, release, cancellationToken).ConfigureAwait(false);
            InstallerMachineCertificateOwnership? ownership = await ReadAsync(release, cancellationToken).ConfigureAwait(false);
            InstallerCertificatePresence presence = await InspectAsync(request, release, cancellationToken).ConfigureAwait(false);
            bool valid = request.Operation == InstallerOperation.Uninstall
                ? ownership is null || (ownership.InstallerOwned && presence == InstallerCertificatePresence.ExactMatch
                    && await _references.HasReferencesAsync(release.Manifest, cancellationToken).ConfigureAwait(false))
                : ownership is not null && presence == InstallerCertificatePresence.ExactMatch;
            if (!valid)
            {
                throw new InstallerProtocolException("installer.machine_certificate.postcondition_failed");
            }
            await ReverifyAsync(request, release, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    private async Task InstallAsync(InstallerRequest request, IInstallerReleaseLease release,
        InstallerMachineCertificateOwnership? ownership, InstallerCertificatePresence presence, CancellationToken cancellationToken)
    {
        if (presence == InstallerCertificatePresence.Missing && !release.Release.CertificatePayloadAvailable)
        {
            throw new InstallerProtocolException("installer.release.certificate_payload_missing");
        }
        if (ownership is null)
        {
            InstallerMachineCertificateOwnership created = InstallerMachineCertificateOwnership.Create(
                release.Manifest, presence == InstallerCertificatePresence.ExactMatch);
            await PersistAsync(release, null, created, cancellationToken).ConfigureAwait(false);
            ownership = created;
        }
        if (presence == InstallerCertificatePresence.ExactMatch)
        {
            return;
        }
        if (!ownership.InstallerOwned)
        {
            InstallerMachineCertificateOwnership claimed = ownership with { InstallerOwned = true };
            await PersistAsync(release, ownership, claimed, cancellationToken).ConfigureAwait(false);
        }
        await ReverifyAsync(request, release, cancellationToken).ConfigureAwait(false);
        await MutateCertificateAsync(request, release, import: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveAsync(InstallerRequest request, IInstallerReleaseLease release,
        InstallerMachineCertificateOwnership? ownership, InstallerCertificatePresence presence, CancellationToken cancellationToken)
    {
        if (ownership is null)
        {
            return;
        }
        if (ownership.InstallerOwned)
        {
            if (await _references.HasReferencesAsync(release.Manifest, cancellationToken).ConfigureAwait(false))
            {
                if (presence == InstallerCertificatePresence.Missing)
                {
                    throw new InstallerStateUncertainException("installer.machine_certificate.referenced_trust_missing");
                }
                return;
            }
        }
        if (ownership.InstallerOwned && presence == InstallerCertificatePresence.ExactMatch)
        {
            await ReverifyAsync(request, release, cancellationToken).ConfigureAwait(false);
            // A second inventory immediately precedes removal. The Windows adapter independently
            // performs the same check; neither snapshot claims to lock unrelated AppX deployments.
            if (await _references.HasReferencesAsync(release.Manifest, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await MutateCertificateAsync(request, release, import: false, cancellationToken).ConfigureAwait(false);
            if (await _references.HasReferencesAsync(release.Manifest, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InstallerStateUncertainException("installer.machine_certificate.references_changed");
            }
        }
        await ReverifyAsync(request, release, cancellationToken).ConfigureAwait(false);
        await PersistAsync(release, ownership, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task MutateCertificateAsync(InstallerRequest request, IInstallerReleaseLease release,
        bool import, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            if (import)
            {
                await _certificates.ImportAsync(request, release, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _certificates.RemoveExactAsync(request, release, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (MayHaveMutated(exception))
        {
            failure = exception;
        }
        InstallerCertificatePresence observed;
        try
        {
            await ReverifyAsync(request, release, CancellationToken.None).ConfigureAwait(false);
            observed = await InspectAsync(request, release, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (MayHaveMutated(exception))
        {
            throw new InstallerStateUncertainException("installer.machine_certificate.mutation_state_uncertain");
        }
        if (observed == (import ? InstallerCertificatePresence.ExactMatch : InstallerCertificatePresence.Missing))
        {
            return;
        }
        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        throw new InstallerStateUncertainException("installer.machine_certificate.mutation_state_uncertain");
    }

    private async Task PersistAsync(IInstallerReleaseLease release, InstallerMachineCertificateOwnership? before,
        InstallerMachineCertificateOwnership? after, CancellationToken cancellationToken)
    {
        if (await ReadAsync(release, cancellationToken).ConfigureAwait(false) != before)
        {
            throw new InstallerProtocolException("installer.machine_certificate.write_conflict");
        }
        byte[]? bytes = after?.Serialize();
        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (bytes is null)
                {
                    await _persistence.DeleteAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _persistence.WriteAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (MayHaveMutated(exception))
            {
                failure = exception;
            }
            InstallerMachineCertificateOwnership? observed;
            try
            {
                observed = await ReadAsync(release, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (MayHaveMutated(exception))
            {
                throw new InstallerStateUncertainException("installer.machine_certificate.write_state_uncertain");
            }
            if (observed == after)
            {
                return;
            }
            if (observed == before && failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            throw new InstallerStateUncertainException("installer.machine_certificate.write_state_uncertain");
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private async Task<InstallerMachineCertificateOwnership?> ReadAsync(IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        byte[]? bytes = await _persistence.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }
        try
        {
            InstallerMachineCertificateOwnership ownership = InstallerMachineCertificateOwnership.Parse(bytes);
            ownership.RequireManifest(release.Manifest);
            return ownership;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<InstallerCertificatePresence> InspectAsync(
        InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        InstallerCertificatePresence presence = await _certificates.InspectAsync(request, release, cancellationToken).ConfigureAwait(false);
        if (presence is not (InstallerCertificatePresence.Missing or InstallerCertificatePresence.ExactMatch))
        {
            throw new InstallerProtocolException("installer.machine_certificate.identity_conflict");
        }
        return presence;
    }

    private static Task ReverifyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Manifest.Validate();
        release.Release.Validate();
        if (!release.Manifest.Matches(release.Release) || request.ExpectedPackageVersion != release.Release.ExpectedPackageVersion
            || request.InstallerPayloadSha256 != release.Release.InstallerPayloadSha256)
        {
            throw new InstallerProtocolException("installer.release.identity_mismatch");
        }
        return release.ReverifyAsync(request, cancellationToken);
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InstallerProtocolException("installer.machine_certificate.operation_in_progress");
        }
    }

    private static bool MayHaveMutated(Exception exception) => exception is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException);
}
