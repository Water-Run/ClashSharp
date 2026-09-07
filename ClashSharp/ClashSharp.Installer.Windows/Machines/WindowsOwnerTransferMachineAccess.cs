using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Transfers the fixed machine trees after the old service is absent. The caller retains private
/// journal authority, both App barriers and the release lease; this step does not advance journals,
/// switch association, create missing roots, change private data or acquire those authorities.
/// </summary>
internal sealed class WindowsOwnerTransferMachineAccess
{
    private readonly IInstallerReleaseLease _release;
    private readonly IWindowsOwnerTransferStateBackend _backend;
    private readonly IWindowsOwnerTransferAccessNative _native;

    internal WindowsOwnerTransferMachineAccess(IInstallerReleaseLease release,
        IWindowsOwnerTransferStateBackend backend,
        IWindowsOwnerTransferAccessNative native)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(native);
        _release = release;
        _backend = backend;
        _native = native;
    }

    internal async Task ApplyAndVerifyAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        if (current.Phase != InstallerOwnerTransferPhase.PreviousServiceRemoved)
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_phase_invalid");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var nextRequest = new InstallerRequest(current.Continuation.Operation,
            current.Continuation.TargetSid, false, current.Continuation.ExpectedPackageVersion,
            current.Continuation.InstallerPayloadSha256);
        try
        {
            await _release.ReverifyAsync(nextRequest, cancellationToken).ConfigureAwait(false);
            string previousProfile = ResolveProfile(current.PreviousOwner, cancellationToken);
            string nextProfile = ResolveProfile(current.NextOwner, cancellationToken);
            var previousRequest = new InstallerRequest(InstallerOperation.Uninstall,
                current.PreviousOwner.Association.OwnerSid, false, nextRequest.ExpectedPackageVersion,
                nextRequest.InstallerPayloadSha256);
            WindowsMachineDeploymentPlan previous = _backend.CreatePlan(
                previousRequest, _release.Manifest, current.PreviousOwner.Association, previousProfile, removalPlan: true);
            WindowsMachineDeploymentPlan next = _backend.CreatePlan(
                nextRequest, _release.Manifest, current.NextOwner.Association, nextProfile, removalPlan: false);
            if (previous.Roots != next.Roots || previous.Association != current.PreviousOwner.Association
                || next.Association != current.NextOwner.Association || previous.Request != previousRequest || next.Request != nextRequest)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_plan_changed");
            }
            _backend.VerifyServiceAbsent(cancellationToken);
            using WindowsOwnerTransferAccessTree tree = WindowsOwnerTransferAccessTree.Acquire(
                next.Roots, _native, current.PreviousOwner.Association.OwnerSid,
                current.NextOwner.Association.OwnerSid, cancellationToken);
            tree.VerifyAssociation(current.PreviousOwner.Association);
            tree.VerifyContinuation(current.Continuation);
            _backend.VerifyServiceAbsent(cancellationToken);
            tree.ApplyAndVerify(cancellationToken);
            tree.VerifyAssociation(current.PreviousOwner.Association);
            tree.VerifyContinuation(current.Continuation);
            await _release.ReverifyAsync(nextRequest, cancellationToken).ConfigureAwait(false);
            _backend.VerifyServiceAbsent(cancellationToken);
            tree.Reverify(requireTransferred: true, cancellationToken);
            tree.VerifyContinuation(current.Continuation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Preserve the durable phase and completed ACL changes for exact replay; never expose
            // native paths or identities in an exception that could cross the helper boundary.
            throw new InstallerProtocolException("installer.owner_transfer.access_failed");
        }
    }

    private string ResolveProfile(InstallerOwnerTransferParticipant participant, CancellationToken cancellationToken)
    {
        string profile = _backend.ResolveTargetProfile(participant.Association.OwnerSid, cancellationToken);
        if (!string.Equals(profile, participant.ProfileRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_profile_changed");
        }
        return profile;
    }

}
