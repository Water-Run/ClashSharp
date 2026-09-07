using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Transfers only the fixed Installer state DACLs after certificate files reach their final state.
/// Caller-owned global authority, both App barriers, private journal and candidate remain held.
/// Failure retains the durable phase and partial ACL changes for replay without changing file bytes.
/// </summary>
internal sealed class WindowsOwnerTransferInstallerAccess
{
    private readonly IInstallerReleaseLease _release;
    private readonly IWindowsOwnerTransferStateBackend _backend;
    private readonly IWindowsOwnerTransferAccessNative _directories;
    private readonly IWindowsOwnerTransferCertificateStateReader _certificates;

    internal WindowsOwnerTransferInstallerAccess(IInstallerReleaseLease release,
        IWindowsOwnerTransferStateBackend backend, IWindowsOwnerTransferAccessNative directories,
        IWindowsOwnerTransferCertificateStateReader certificates)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(certificates);
        _release = release;
        _backend = backend;
        _directories = directories;
        _certificates = certificates;
    }

    internal async Task ApplyAndVerifyAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        if (current.Phase != InstallerOwnerTransferPhase.CertificateStateTransferred)
        {
            throw new InstallerProtocolException("installer.owner_transfer.installer_access_phase_invalid");
        }
        cancellationToken.ThrowIfCancellationRequested();
        InstallerRequest request = WindowsOwnerTransferDeployment.CreateContinuationRequest(current);
        try
        {
            await _release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (current.NextCertificateLedger is { } next && !next.Matches(request, _release.Release))
            {
                throw new InstallerProtocolException("installer.owner_transfer.certificate_release_conflict");
            }
            WindowsMachineDeploymentPlan deployment = WindowsOwnerTransferDeployment.ResolveNextPlan(
                current, _release.Manifest, _backend, cancellationToken);
            var plan = new WindowsOwnerTransferCertificatePlan(deployment.Roots, current);
            _backend.VerifyServiceAbsent(cancellationToken);
            using WindowsOwnerTransferAccessTree tree =
                WindowsOwnerTransferAccessTree.AcquireForInstallerAccess(plan, _directories, cancellationToken);
            await VerifyStateAsync(tree, plan, requireTransferred: false, cancellationToken).ConfigureAwait(false);
            _backend.VerifyServiceAbsent(cancellationToken);
            tree.ApplyAndVerify(cancellationToken);
            await VerifyStateAsync(tree, plan, requireTransferred: true, cancellationToken).ConfigureAwait(false);
            await _release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (WindowsOwnerTransferDeployment.ResolveNextPlan(current, _release.Manifest, _backend, cancellationToken).Roots != deployment.Roots)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_plan_changed");
            }
            _backend.VerifyServiceAbsent(cancellationToken);
            await VerifyStateAsync(tree, plan, requireTransferred: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.owner_transfer.installer_access_failed");
        }
    }

    private async Task VerifyStateAsync(WindowsOwnerTransferAccessTree tree,
        WindowsOwnerTransferCertificatePlan plan, bool requireTransferred, CancellationToken cancellationToken)
    {
        tree.Reverify(requireTransferred, cancellationToken);
        plan.VerifyCompletedState(await _certificates.ReadAsync(plan, cancellationToken).ConfigureAwait(false));
        // Retain all handles through owned observation and verify again after asynchronous work.
        tree.VerifyAssociation(plan.Journal.NextOwner.Association);
        tree.VerifyContinuation(plan.Journal.Continuation);
        tree.Reverify(requireTransferred, cancellationToken);
    }
}
