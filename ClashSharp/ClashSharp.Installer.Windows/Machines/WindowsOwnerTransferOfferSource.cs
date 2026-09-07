using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsOwnerTransferInspection : IDisposable
{
    Task<InstallerOwnerTransferSnapshot?> LoadPrivateAsync(CancellationToken cancellationToken);
    Task<InstallerOwnerTransferJournal> CaptureNewAsync(
        InstallerRequest request, InstallerReleaseManifest manifest, CancellationToken cancellationToken);
}

/// <summary>
/// Takes a consistent read-only inspection under machine exclusion. The confirmation gap owns no
/// mutable state or App barriers; the authority factory rechecks every captured field after consent.
/// </summary>
internal sealed class WindowsOwnerTransferOfferSource : IWindowsOwnerTransferOfferSource
{
    private readonly IWindowsInstallerAuthorityLock _authorityLock;
    private readonly IInstallerReleaseVerifier _release;
    private readonly IWindowsOwnerTransferStateBackend _backend;
    private readonly Func<IWindowsOwnerTransferInspection> _inspection;

    internal WindowsOwnerTransferOfferSource(IWindowsInstallerAuthorityLock authorityLock,
        IInstallerReleaseVerifier release, IWindowsOwnerTransferStateBackend backend,
        Func<IWindowsOwnerTransferInspection> inspection)
    {
        ArgumentNullException.ThrowIfNull(authorityLock);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(inspection);
        _authorityLock = authorityLock;
        _release = release;
        _backend = backend;
        _inspection = inspection;
    }

    public async Task<WindowsOwnerTransferConfirmedState> CaptureAsync(InstallerOwnerTransferRequest request,
        string authenticatedTargetSid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstallerRequest ordinary = request.ForAuthenticatedAccount(authenticatedTargetSid);
        cancellationToken.ThrowIfCancellationRequested();
        await using IAsyncDisposable authority = await _authorityLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using IInstallerReleaseLease release = await _release.VerifyAsync(ordinary, cancellationToken).ConfigureAwait(false);
        await release.ReverifyAsync(ordinary, cancellationToken).ConfigureAwait(false);
        using IWindowsOwnerTransferInspection inspection = _inspection()
            ?? throw new InstallerProtocolException("installer.owner_transfer.inspection_missing");
        InstallerOwnerTransferSnapshot? current = await inspection.LoadPrivateAsync(cancellationToken).ConfigureAwait(false);
        InstallerOwnerTransferJournal journal = current?.Journal
            ?? await inspection.CaptureNewAsync(ordinary, release.Manifest, cancellationToken).ConfigureAwait(false);
        var confirmed = new WindowsOwnerTransferConfirmedState(journal, current);
        confirmed.Validate(authenticatedTargetSid);
        InstallerRequest continuationRequest = current is null ? ordinary : ordinary with { Operation = journal.Continuation.Operation };
        if (!journal.Continuation.Matches(continuationRequest))
        {
            throw new InstallerProtocolException("installer.owner_transfer.candidate_mismatch");
        }
        if (journal.NextCertificateLedger is { } next && !next.Matches(ordinary, release.Release))
        {
            throw new InstallerProtocolException("installer.owner_transfer.target_certificate_mismatch");
        }
        _ = WindowsOwnerTransferDeployment.ResolveNextPlan(journal, release.Manifest, _backend, cancellationToken);
        await release.ReverifyAsync(ordinary, cancellationToken).ConfigureAwait(false);
        return confirmed;
    }
}
