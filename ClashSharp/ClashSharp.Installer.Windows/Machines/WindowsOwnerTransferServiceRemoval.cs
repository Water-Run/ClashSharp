using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsOwnerTransferServiceBackend : IWindowsOwnerTransferStateBackend
{
    IWindowsMachineRootGuard CreateRootGuard(WindowsMachineDeploymentPlan plan, bool createMissing);

    IWindowsMachineAssociationStore CreateAssociationStore(WindowsMachineDeploymentPlan plan, IWindowsMachineRootGuard rootGuard);

    Task StopDeleteServiceAsync(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// Removes only the service bound to the recorded previous owner after the ordinary startup
/// barrier is observed. The dedicated transfer authority owns the candidate, both App barriers
/// and machine exclusion; this step preserves association and payload evidence for later phases.
/// </summary>
internal sealed class WindowsOwnerTransferServiceRemoval
{
    private readonly IInstallerReleaseLease _release;
    private readonly IInstallerTransactionReader _ordinaryContinuation;
    private readonly IWindowsOwnerTransferServiceBackend _backend;

    internal WindowsOwnerTransferServiceRemoval(
        IInstallerReleaseLease release,
        IInstallerTransactionReader ordinaryContinuation,
        IWindowsOwnerTransferServiceBackend backend)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(ordinaryContinuation);
        ArgumentNullException.ThrowIfNull(backend);
        _release = release;
        _ordinaryContinuation = ordinaryContinuation;
        _backend = backend;
    }

    internal async Task ApplyAndVerifyAsync(
        InstallerOwnerTransferJournal current,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        if (current.Phase != InstallerOwnerTransferPhase.StartupBlocked)
        {
            throw new InstallerProtocolException("installer.owner_transfer.service_phase_invalid");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var continuationRequest = new InstallerRequest(
            current.Continuation.Operation, current.Continuation.TargetSid, false,
            current.Continuation.ExpectedPackageVersion, current.Continuation.InstallerPayloadSha256);
        await _release.ReverifyAsync(continuationRequest, cancellationToken).ConfigureAwait(false);
        InstallerTransactionSnapshot? barrier = await _ordinaryContinuation.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (barrier is null || barrier != InstallerTransactionSnapshot.Create(current.Continuation))
        {
            throw new InstallerProtocolException("installer.owner_transfer.continuation_changed");
        }

        string previousProfile = _backend.ResolveTargetProfile(current.PreviousOwner.Association.OwnerSid, cancellationToken);
        if (!string.Equals(previousProfile, current.PreviousOwner.ProfileRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException("installer.owner_transfer.previous_profile_changed");
        }

        var previousRequest = new InstallerRequest(
            InstallerOperation.Uninstall, current.PreviousOwner.Association.OwnerSid, false,
            current.Continuation.ExpectedPackageVersion, current.Continuation.InstallerPayloadSha256);
        WindowsMachineDeploymentPlan plan = _backend.CreatePlan(
            previousRequest, _release.Manifest, current.PreviousOwner.Association, previousProfile, removalPlan: true);
        using IWindowsMachineRootGuard roots = _backend.CreateRootGuard(plan, createMissing: false);
        await roots.EnsureProtectedAsync(plan, cancellationToken).ConfigureAwait(false);
        using IWindowsMachineAssociationStore association = _backend.CreateAssociationStore(plan, roots);
        await association.VerifyExactAsync(cancellationToken).ConfigureAwait(false);

        await _backend.StopDeleteServiceAsync(plan, cancellationToken).ConfigureAwait(false);
        _backend.VerifyServiceAbsent(cancellationToken);
        await association.VerifyExactAsync(cancellationToken).ConfigureAwait(false);
    }
}
