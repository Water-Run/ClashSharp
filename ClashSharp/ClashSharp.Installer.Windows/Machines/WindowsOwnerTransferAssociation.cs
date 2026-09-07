using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Transfers the recorded association after shared ACL migration, with the ordinary barrier and
/// preserved machine tree pinned through post-error observation. The caller continuously owns the
/// private journal, global authority, both App barriers and release; this step never advances them.
/// </summary>
internal sealed class WindowsOwnerTransferAssociation
{
    private readonly IInstallerReleaseLease _release;
    private readonly IWindowsOwnerTransferStateBackend _backend;
    private readonly IWindowsOwnerTransferAccessNative _directories;
    private readonly IWindowsOwnerTransferAssociationFileNative _files;

    internal WindowsOwnerTransferAssociation(IInstallerReleaseLease release,
        IWindowsOwnerTransferStateBackend backend, IWindowsOwnerTransferAccessNative directories,
        IWindowsOwnerTransferAssociationFileNative files)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(files);
        _release = release;
        _backend = backend;
        _directories = directories;
        _files = files;
    }

    internal async Task ApplyAndVerifyAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        if (current.Phase != InstallerOwnerTransferPhase.MachineAccessTransferred)
        {
            throw new InstallerProtocolException("installer.owner_transfer.association_phase_invalid");
        }
        cancellationToken.ThrowIfCancellationRequested();
        InstallerRequest request = WindowsOwnerTransferDeployment.CreateContinuationRequest(current);
        try
        {
            await _release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            WindowsMachineDeploymentPlan deployment = WindowsOwnerTransferDeployment.ResolveNextPlan(
                current, _release.Manifest, _backend, cancellationToken);
            var plan = new WindowsOwnerTransferAssociationPlan(deployment.Roots,
                current.Continuation.TransactionId, current.PreviousOwner.Association, current.NextOwner.Association);
            plan.Validate();
            _backend.VerifyServiceAbsent(cancellationToken);
            using IWindowsOwnerTransferAssociationBoundary boundary =
                WindowsOwnerTransferAccessTree.AcquireForAssociationTransfer(plan, _directories, cancellationToken);
            boundary.VerifyContinuation(current.Continuation);
            WindowsOwnerTransferAssociationObservation before = await _files.InspectAsync(plan, cancellationToken).ConfigureAwait(false);
            if (before.Association != plan.Previous && before.Association != plan.Next)
            {
                throw new InstallerProtocolException("installer.owner_transfer.association_conflict");
            }
            boundary.Reverify(cancellationToken);
            boundary.VerifyContinuation(current.Continuation);
            _backend.VerifyServiceAbsent(cancellationToken);
            if (before.Association != plan.Next || before.TemporaryPresent)
            {
                await ReplaceAndReconcileAsync(plan, boundary, current, cancellationToken).ConfigureAwait(false);
            }
            await _release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            _backend.VerifyServiceAbsent(cancellationToken);
            WindowsOwnerTransferAssociationObservation after = await _files.InspectAsync(plan, cancellationToken).ConfigureAwait(false);
            if (after.Association != plan.Next || after.TemporaryPresent)
            {
                throw new InstallerProtocolException("installer.owner_transfer.association_postcondition_failed");
            }
            boundary.Reverify(cancellationToken);
            boundary.VerifyContinuation(current.Continuation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.owner_transfer.association_failed");
        }
    }

    private async Task ReplaceAndReconcileAsync(WindowsOwnerTransferAssociationPlan plan,
        IWindowsOwnerTransferAssociationBoundary boundary, InstallerOwnerTransferJournal current,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = null;
        try
        {
            await _files.ReplaceExactAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            failure = exception;
        }

        WindowsOwnerTransferAssociationObservation observed;
        try
        {
            // A cancellation/exception can arrive after atomic replacement. Do not release the
            // pinned barrier or accept the acknowledgement until actual state has been observed.
            boundary.Reverify(CancellationToken.None);
            boundary.VerifyContinuation(current.Continuation);
            observed = await _files.InspectAsync(plan, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerStateUncertainException("installer.owner_transfer.association_state_uncertain");
        }
        if (observed.Association == plan.Next && !observed.TemporaryPresent)
        {
            return;
        }
        if (observed.Association == plan.Previous && failure is OperationCanceledException
            && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        throw new InstallerStateUncertainException("installer.owner_transfer.association_state_uncertain");
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception or InstallerProtocolException
            or InstallerStateUncertainException or OperationCanceledException;
}
