using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Observes the complete transfer before the coordinator clears private authority evidence.
/// This port acquires only read access and cannot repair an incomplete tree or advance either journal.
/// </summary>
internal sealed class WindowsOwnerTransferCompletion
{
    private readonly IInstallerReleaseLease _release;
    private readonly IWindowsOwnerTransferStateBackend _backend;
    private readonly IWindowsOwnerTransferAccessNative _directories;
    private readonly IWindowsOwnerTransferCertificateStateReader _certificates;

    internal WindowsOwnerTransferCompletion(IInstallerReleaseLease release,
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

    internal async Task VerifyAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        if (current.Phase is not (InstallerOwnerTransferPhase.InstallerAccessTransferred or InstallerOwnerTransferPhase.Verified))
        {
            throw new InstallerProtocolException("installer.owner_transfer.completion_phase_invalid");
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
            using IWindowsOwnerTransferCertificateBoundary boundary =
                WindowsOwnerTransferAccessTree.AcquireForCompletedTransfer(plan, _directories, cancellationToken);
            await VerifyStateAsync(boundary, plan, cancellationToken).ConfigureAwait(false);
            await _release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (WindowsOwnerTransferDeployment.ResolveNextPlan(current, _release.Manifest, _backend, cancellationToken).Roots != deployment.Roots)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_plan_changed");
            }
            _backend.VerifyServiceAbsent(cancellationToken);
            await VerifyStateAsync(boundary, plan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.owner_transfer.completion_failed");
        }
    }

    private async Task VerifyStateAsync(IWindowsOwnerTransferCertificateBoundary boundary,
        WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken)
    {
        boundary.Reverify(cancellationToken);
        plan.VerifyCompletedState(await _certificates.ReadAsync(plan, cancellationToken).ConfigureAwait(false));
        boundary.VerifyAssociation(plan.Journal.NextOwner.Association);
        boundary.VerifyContinuation(plan.Journal.Continuation);
        boundary.Reverify(cancellationToken);
    }
}
