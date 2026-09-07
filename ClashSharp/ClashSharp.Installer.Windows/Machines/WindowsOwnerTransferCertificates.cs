using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Preserves both accounts' live certificate ownership without touching either certificate store.
/// The caller retains private durable evidence, global authority, both App barriers and release
/// through all file mutations and uncancelled post-error observations.
/// </summary>
internal sealed class WindowsOwnerTransferCertificates
{
    private readonly IInstallerReleaseLease _release;
    private readonly IWindowsOwnerTransferStateBackend _backend;
    private readonly IWindowsOwnerTransferAccessNative _directories;
    private readonly IWindowsOwnerTransferCertificateFileNative _files;

    internal WindowsOwnerTransferCertificates(IInstallerReleaseLease release,
        IWindowsOwnerTransferStateBackend backend, IWindowsOwnerTransferAccessNative directories,
        IWindowsOwnerTransferCertificateFileNative files)
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
        if (current.Phase != InstallerOwnerTransferPhase.AssociationTransferred)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_phase_invalid");
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
            plan.Validate();
            _backend.VerifyServiceAbsent(cancellationToken);
            using IWindowsOwnerTransferCertificateBoundary boundary =
                WindowsOwnerTransferAccessTree.AcquireForCertificateTransfer(plan, _directories, cancellationToken);
            VerifyBoundary(boundary, current, cancellationToken);
            InstallerOwnerTransferCertificateState observed = await _files.ReadAsync(plan, cancellationToken).ConfigureAwait(false);
            while (observed.GetNextStep(current) is { } step)
            {
                VerifyBoundary(boundary, current, cancellationToken);
                _backend.VerifyServiceAbsent(cancellationToken);
                observed = await ApplyAndReconcileAsync(plan, observed, step, boundary, cancellationToken).ConfigureAwait(false);
            }
            await _release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            _backend.VerifyServiceAbsent(cancellationToken);
            observed = await _files.ReadAsync(plan, cancellationToken).ConfigureAwait(false);
            if (observed.GetNextStep(current) is not null)
            {
                throw new InstallerProtocolException("installer.owner_transfer.certificate_postcondition_failed");
            }
            VerifyBoundary(boundary, current, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_failed");
        }
    }

    private async Task<InstallerOwnerTransferCertificateState> ApplyAndReconcileAsync(
        WindowsOwnerTransferCertificatePlan plan, InstallerOwnerTransferCertificateState before,
        InstallerOwnerTransferCertificateStep step, IWindowsOwnerTransferCertificateBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = null;
        try
        {
            await _files.ApplyAsync(plan, before, step, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            failure = exception;
        }
        InstallerOwnerTransferCertificateState observed;
        try
        {
            VerifyBoundary(boundary, plan.Journal, CancellationToken.None);
            observed = await _files.ReadAsync(plan, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerStateUncertainException("installer.owner_transfer.certificate_state_uncertain");
        }
        if (observed == step.After)
        {
            return observed;
        }
        if (observed == before && failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        throw new InstallerStateUncertainException("installer.owner_transfer.certificate_state_uncertain");
    }

    private static void VerifyBoundary(IWindowsOwnerTransferCertificateBoundary boundary,
        InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
    {
        boundary.Reverify(cancellationToken);
        boundary.VerifyContinuation(current.Continuation);
        boundary.VerifyAssociation(current.NextOwner.Association);
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception or InstallerProtocolException
            or InstallerStateUncertainException or OperationCanceledException;
}
