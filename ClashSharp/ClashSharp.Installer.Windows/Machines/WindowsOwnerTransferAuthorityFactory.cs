using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Helper-private state captured by the dedicated inspection/confirmation exchange. It contains
/// credentials and must never be populated from, or serialized onto, the parent pipe.
/// </summary>
internal sealed record WindowsOwnerTransferConfirmedState(
    InstallerOwnerTransferJournal Journal, InstallerOwnerTransferSnapshot? ExpectedPrivateState)
{
    internal void Validate(string authenticatedTargetSid)
    {
        ArgumentNullException.ThrowIfNull(Journal);
        Journal.Validate();
        InstallerProtocolValidation.ValidateTargetSid(authenticatedTargetSid);
        if (Journal.NextOwner.Association.OwnerSid != authenticatedTargetSid)
        {
            throw new InstallerProtocolException("installer.owner_transfer.target_sid_mismatch");
        }
        if (ExpectedPrivateState is { } expected)
        {
            expected.Validate();
            if (expected.Journal != Journal)
            {
                throw new InstallerProtocolException("installer.owner_transfer.confirmation_state_changed");
            }
        }
        else if (Journal.Phase != InstallerOwnerTransferPhase.Prepared)
        {
            throw new InstallerProtocolException("installer.owner_transfer.confirmation_state_invalid");
        }
    }

    public override string ToString() => "WindowsOwnerTransferConfirmedState { Private helper evidence }";
}

/// <summary>Returns ordinary authority while retaining the transfer's global, dual-App and candidate leases.</summary>
internal sealed record WindowsOwnerTransferHandoff(
    InstallerTransactionSnapshot Continuation, IWindowsMachineHelperAuthorityLease Authority);

/// <summary>
/// Runs confirmed new/recovered transfer state inside one machine-exclusive scope. Authentication
/// and explicit consent are prerequisites supplied only by the dedicated host; ordinary flags never
/// reach this factory. A successful handoff transfers every lease without an unlock/relaunch gap.
/// </summary>
internal interface IWindowsOwnerTransferAuthorityFactory
{
    Task<WindowsOwnerTransferHandoff> CreateAsync(WindowsOwnerTransferConfirmedState confirmed,
        string authenticatedTargetSid, CancellationToken cancellationToken);
}

internal sealed class WindowsOwnerTransferAuthorityFactory : IWindowsOwnerTransferAuthorityFactory
{
    private readonly IWindowsInstallerAuthorityLock _authorityLock;
    private readonly IWindowsInstallerApplicationLock _applicationLock;
    private readonly IInstallerReleaseVerifier _releaseVerifier;
    private readonly IWindowsOwnerTransferServiceBackend _backend;
    private readonly IWindowsOwnerTransferAuthorityResourcesFactory _transferFactory;
    private readonly IWindowsMachineHelperAuthorityResourcesFactory _ordinaryFactory;
    private readonly IWindowsInstallerRetiredUninstallAdmission _retiredUninstallAdmission;

    internal WindowsOwnerTransferAuthorityFactory(IWindowsInstallerAuthorityLock authorityLock,
        IWindowsInstallerApplicationLock applicationLock, IInstallerReleaseVerifier releaseVerifier,
        IWindowsOwnerTransferServiceBackend backend, IWindowsOwnerTransferAuthorityResourcesFactory transferFactory,
        IWindowsMachineHelperAuthorityResourcesFactory ordinaryFactory,
        IWindowsInstallerRetiredUninstallAdmission retiredUninstallAdmission)
    {
        ArgumentNullException.ThrowIfNull(authorityLock);
        ArgumentNullException.ThrowIfNull(applicationLock);
        ArgumentNullException.ThrowIfNull(releaseVerifier);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(transferFactory);
        ArgumentNullException.ThrowIfNull(ordinaryFactory);
        ArgumentNullException.ThrowIfNull(retiredUninstallAdmission);
        _authorityLock = authorityLock;
        _applicationLock = applicationLock;
        _releaseVerifier = releaseVerifier;
        _backend = backend;
        _transferFactory = transferFactory;
        _ordinaryFactory = ordinaryFactory;
        _retiredUninstallAdmission = retiredUninstallAdmission;
    }

    internal static WindowsOwnerTransferAuthorityFactory CreateDefault(
        ReadOnlyMemory<byte> embeddedManifestBytes, string installerExecutablePath)
    {
        byte[] manifestBytes = embeddedManifestBytes.ToArray();
        var backend = new WindowsMachineHelperMachineBackend();
        return new(new WindowsInstallerAuthorityLock(), WindowsInstallerApplicationLock.CreateHelper(),
            new WindowsInstallerReleaseVerifier(manifestBytes, installerExecutablePath), backend,
            new WindowsOwnerTransferAuthorityResourcesFactory(backend),
            new WindowsMachineHelperAuthorityResourcesFactory(store =>
                WindowsMachineHelperOperationExecutor.CreateDefault(manifestBytes, store)),
            WindowsInstallerRetiredUninstallAdmission.CreateDefault());
    }

    public async Task<WindowsOwnerTransferHandoff> CreateAsync(
        WindowsOwnerTransferConfirmedState confirmed, string authenticatedTargetSid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        confirmed.Validate(authenticatedTargetSid);
        cancellationToken.ThrowIfCancellationRequested();
        InstallerOwnerTransferJournal journal = confirmed.Journal;
        InstallerRequest request = WindowsOwnerTransferDeployment.CreateContinuationRequest(journal);
        var scope = new WindowsInstallerAuthorityScope();
        try
        {
            _ = scope.RetainAsync(await _authorityLock.AcquireAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerProtocolException("installer.owner_transfer.authority_missing"));
            await _retiredUninstallAdmission.EnsureNoRetiredUninstallAsync(cancellationToken).ConfigureAwait(false);
            IInstallerReleaseLease release = scope.RetainAsync(await _releaseVerifier.VerifyAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerProtocolException("installer.release.lease_missing"));
            await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (journal.NextCertificateLedger is { } nextLedger && !nextLedger.Matches(request, release.Release))
            {
                throw new InstallerProtocolException("installer.owner_transfer.target_certificate_mismatch");
            }
            WindowsMachineDeploymentPlan deployment = WindowsOwnerTransferDeployment.ResolveNextPlan(
                journal, release.Manifest, _backend, cancellationToken);
            _ = scope.Retain(_applicationLock.Acquire(journal.PreviousOwner.Association.OwnerSid, cancellationToken)
                ?? throw new InstallerProtocolException("installer.application_lock.lease_missing"));
            _ = scope.Retain(_applicationLock.Acquire(authenticatedTargetSid, cancellationToken)
                ?? throw new InstallerProtocolException("installer.application_lock.lease_missing"));
            // Acquiring the App barriers may wait. Re-resolve both identities before opening
            // writable private state so a changed profile cannot publish a stranded Prepared.
            if (WindowsOwnerTransferDeployment.ResolveNextPlan(journal, release.Manifest, _backend, cancellationToken).Roots != deployment.Roots)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_plan_changed");
            }
            IWindowsOwnerTransferAuthorityResources transfer = scope.Retain(
                _transferFactory.Create(journal, release, newTransfer: confirmed.ExpectedPrivateState is null)
                ?? throw new InstallerProtocolException("installer.owner_transfer.resources_missing"));
            InstallerOwnerTransferSnapshot? current = await transfer.Store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (current != confirmed.ExpectedPrivateState)
            {
                throw new InstallerProtocolException("installer.owner_transfer.confirmation_state_changed");
            }
            if (current is null)
            {
                await transfer.VerifyNewPreparationAsync(deployment.Roots, journal, cancellationToken).ConfigureAwait(false);
                current = await transfer.Store.SaveAsync(journal, null, cancellationToken).ConfigureAwait(false);
            }
            InstallerTransactionSnapshot continuation = await new InstallerOwnerTransferCoordinator(transfer.Store, transfer.Executor)
                .ResumeAsync(current, cancellationToken).ConfigureAwait(false);
            if (continuation != InstallerTransactionSnapshot.Create(journal.Continuation))
            {
                throw new InstallerProtocolException("installer.owner_transfer.continuation_changed");
            }
            await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (WindowsOwnerTransferDeployment.ResolveNextPlan(journal, release.Manifest, _backend, cancellationToken).Roots != deployment.Roots)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_plan_changed");
            }
            // The private journal is now absent and ordinary Prepared is still present. Construct
            // next-owner stores without invoking the ordinary factory's lock/admission acquisition.
            IWindowsMachineHelperAuthorityResources ordinary = scope.RetainAsync(_ordinaryFactory.Create(authenticatedTargetSid)
                ?? throw new InstallerProtocolException("installer.machine_helper.authority_resources_missing"));
            if (await ordinary.TransactionStore.LoadAsync(cancellationToken).ConfigureAwait(false) != continuation)
            {
                throw new InstallerProtocolException("installer.owner_transfer.continuation_changed");
            }
            InstallerMachineHelperAuthoritySession session = await InstallerMachineHelperAuthoritySession.CreateAsync(
                InstallerMachineHelperInvocation.Create(InstallerMachineHelperVerb.Prepare, continuation), authenticatedTargetSid,
                ordinary.TransactionStore, ordinary.Operations, cancellationToken).ConfigureAwait(false);
            return new(continuation, new AuthorityLease(session, scope));
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class AuthorityLease(InstallerMachineHelperAuthoritySession session, WindowsInstallerAuthorityScope scope)
        : IWindowsMachineHelperAuthorityLease
    {
        public InstallerMachineHelperAuthoritySession Session { get; } = session;
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

}
