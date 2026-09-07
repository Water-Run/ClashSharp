using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>Resolves recorded profiles, fixed machine layout and service absence without mutation.</summary>
internal interface IWindowsOwnerTransferStateBackend
{
    string ResolveTargetProfile(string targetSid, CancellationToken cancellationToken);

    WindowsMachineDeploymentPlan CreatePlan(
        InstallerRequest request, InstallerReleaseManifest manifest,
        InstallerMachineAssociation association, string targetProfileRoot, bool removalPlan);

    void VerifyServiceAbsent(CancellationToken cancellationToken);
}
