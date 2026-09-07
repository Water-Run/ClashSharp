using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>Resolves both recorded identities against the same fixed deployment without mutation.</summary>
internal static class WindowsOwnerTransferDeployment
{
    internal static InstallerRequest CreateContinuationRequest(InstallerOwnerTransferJournal current) =>
        new(current.Continuation.Operation, current.Continuation.TargetSid, false,
            current.Continuation.ExpectedPackageVersion, current.Continuation.InstallerPayloadSha256);

    internal static WindowsMachineDeploymentPlan ResolveNextPlan(InstallerOwnerTransferJournal current,
        InstallerReleaseManifest manifest, IWindowsOwnerTransferStateBackend backend, CancellationToken cancellationToken)
    {
        string previousProfile = ResolveProfile(current.PreviousOwner, backend, cancellationToken);
        string nextProfile = ResolveProfile(current.NextOwner, backend, cancellationToken);
        InstallerRequest nextRequest = CreateContinuationRequest(current);
        var previousRequest = new InstallerRequest(InstallerOperation.Uninstall,
            current.PreviousOwner.Association.OwnerSid, false, nextRequest.ExpectedPackageVersion,
            nextRequest.InstallerPayloadSha256);
        WindowsMachineDeploymentPlan previous = backend.CreatePlan(
            previousRequest, manifest, current.PreviousOwner.Association, previousProfile, removalPlan: true);
        WindowsMachineDeploymentPlan next = backend.CreatePlan(
            nextRequest, manifest, current.NextOwner.Association, nextProfile, removalPlan: false);
        if (previous.Roots != next.Roots || previous.Association != current.PreviousOwner.Association
            || next.Association != current.NextOwner.Association || previous.Request != previousRequest || next.Request != nextRequest
            || !string.Equals(previous.TargetProfileRoot, previousProfile, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(next.TargetProfileRoot, nextProfile, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_plan_changed");
        }
        return next;
    }

    private static string ResolveProfile(InstallerOwnerTransferParticipant participant,
        IWindowsOwnerTransferStateBackend backend, CancellationToken cancellationToken)
    {
        string profile = backend.ResolveTargetProfile(participant.Association.OwnerSid, cancellationToken);
        if (!string.Equals(profile, participant.ProfileRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_profile_changed");
        }
        return profile;
    }
}
