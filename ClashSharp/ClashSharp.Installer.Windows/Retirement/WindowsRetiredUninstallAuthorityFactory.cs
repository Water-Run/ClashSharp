using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Certificates;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Retirement;

internal interface IWindowsRetiredUninstallAuthorityFactory
{
    Task<WindowsRetiredUninstallHandoff> CreateAsync(InstallerRequest request, CancellationToken cancellationToken);
}

internal sealed record WindowsRetiredUninstallHandoff(InstallerTransactionSnapshot Ready, IWindowsMachineHelperAuthorityLease Authority);

internal interface IWindowsRetiredUninstallResources : IDisposable
{
    IInstallerTransactionStore Store { get; }
    IInstallerMachineHelperOperationExecutor Operations { get; }
}

internal interface IWindowsRetiredUninstallResourcesFactory
{
    IWindowsRetiredUninstallResources Create(InstallerRequest request, IInstallerReleaseLease release,
        IWindowsRetiredUninstallSharedState shared);
}

/// <summary>
/// Authentication precedes this factory. It retains machine exclusion, only the old account's App
/// barrier, candidate and read-only shared evidence across every phase and durable recovery write.
/// </summary>
internal sealed class WindowsRetiredUninstallAuthorityFactory(
    IWindowsInstallerAuthorityLock authorityLock, IWindowsInstallerApplicationLock applicationLock,
    IWindowsInstallerOwnerTransferAdmission transferAdmission, IInstallerReleaseVerifier releaseVerifier,
    Func<string, IWindowsRetiredUninstallSharedState> sharedFactory, IWindowsRetiredUninstallResourcesFactory resourcesFactory)
    : IWindowsRetiredUninstallAuthorityFactory
{
    internal static WindowsRetiredUninstallAuthorityFactory CreateDefault(ReadOnlyMemory<byte> manifest, string executablePath) =>
        new(new WindowsInstallerAuthorityLock(), WindowsInstallerApplicationLock.CreateHelper(),
            WindowsInstallerOwnerTransferAdmission.CreateDefault(), new WindowsInstallerReleaseVerifier(manifest, executablePath),
            WindowsRetiredUninstallSharedState.CreateDefault, new WindowsRetiredUninstallResourcesFactory());

    public async Task<WindowsRetiredUninstallHandoff> CreateAsync(InstallerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Operation != InstallerOperation.Uninstall || request.AllowReassociation)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.operation_invalid");
        }
        var scope = new WindowsInstallerAuthorityScope();
        try
        {
            _ = scope.RetainAsync(await authorityLock.AcquireAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerProtocolException("installer.retired_uninstall.authority_missing"));
            await transferAdmission.EnsureOrdinaryActionAllowedAsync(cancellationToken).ConfigureAwait(false);
            _ = scope.Retain(applicationLock.Acquire(request.TargetSid, cancellationToken)
                ?? throw new InstallerProtocolException("installer.application_lock.lease_missing"));
            IInstallerReleaseLease release = scope.RetainAsync(await releaseVerifier.VerifyAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerProtocolException("installer.release.lease_missing"));
            await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            IWindowsRetiredUninstallSharedState shared = scope.Retain(sharedFactory(request.TargetSid));
            await shared.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            IWindowsRetiredUninstallResources resources = scope.Retain(resourcesFactory.Create(request, release, shared));
            InstallerTransactionSnapshot? ready = await resources.Store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (ready is null)
            {
                ready = await resources.Store.SaveAsync(InstallerTransactionJournal.Create(request), null, cancellationToken).ConfigureAwait(false);
            }
            InstallerRetiredUninstallProtocol.Validate(ready);
            if (!ready.Journal.Matches(request))
            {
                throw new InstallerProtocolException("installer.retired_uninstall.candidate_mismatch");
            }
            await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            await shared.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            InstallerMachineHelperAuthoritySession session = await InstallerMachineHelperAuthoritySession.CreateAsync(
                InstallerRetiredUninstallProtocol.FirstInvocation(ready), request.TargetSid, resources.Store,
                resources.Operations, cancellationToken).ConfigureAwait(false);
            return new(ready, new AuthorityLease(session, scope));
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

internal sealed class WindowsRetiredUninstallResourcesFactory : IWindowsRetiredUninstallResourcesFactory
{
    public IWindowsRetiredUninstallResources Create(InstallerRequest request, IInstallerReleaseLease release,
        IWindowsRetiredUninstallSharedState shared)
    {
        var journal = WindowsInstallerRetiredUninstallPersistence.CreateDefault(createPrivateRoot: true);
        var archive = WindowsInstallerArchivedCertificatePersistence.CreateDefault(request.TargetSid);
        var packages = new WindowsTargetUserPackageCommitInspector(new WindowsPackageManagerFacade());
        var boundary = new WindowsRetiredUninstallCertificateBoundary(request, release, shared, packages);
        var removal = new InstallerArchivedCertificateRemoval(request.TargetSid,
            new InstallerArchivedCertificateStore(request.TargetSid, archive),
            new WindowsArchivedCertificateRemovalAdapter(request.TargetSid), boundary);
        return new Resources(new InstallerRetiredUninstallStore(request.TargetSid, journal),
            new WindowsRetiredUninstallOperations(request, release, shared, packages, removal), journal, archive);
    }

    private sealed class Resources(IInstallerTransactionStore store, IInstallerMachineHelperOperationExecutor operations,
        IDisposable journal, IDisposable archive) : IWindowsRetiredUninstallResources
    {
        public IInstallerTransactionStore Store { get; } = store;
        public IInstallerMachineHelperOperationExecutor Operations { get; } = operations;
        public void Dispose()
        {
            try { archive.Dispose(); }
            finally { journal.Dispose(); }
        }
    }
}
