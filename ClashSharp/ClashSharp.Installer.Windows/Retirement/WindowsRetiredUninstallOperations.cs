using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Retirement;

/// <summary>
/// Account-copy removal has no shared machine mutation port. Prepare/Remove are preservation
/// checkpoints; CommitPackage independently proves AppXSVC absence before releasing archived trust.
/// </summary>
internal sealed class WindowsRetiredUninstallOperations(
    InstallerRequest request, IInstallerReleaseLease release, IWindowsRetiredUninstallSharedState shared,
    IWindowsTargetUserPackageCommitInspector packages, InstallerArchivedCertificateRemoval certificates)
    : IInstallerMachineHelperOperationExecutor
{
    public async Task ExecuteAsync(InstallerMachineHelperCommand command,
        InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken)
    {
        command.Validate();
        InstallerRetiredUninstallProtocol.Validate(command.ToDurableState());
        if (!command.ToDurableState().Journal.Matches(request) || !Enum.IsDefined(disposition))
        {
            throw new InstallerProtocolException("installer.retired_uninstall.command_invalid");
        }
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        await shared.ReverifyAsync(cancellationToken).ConfigureAwait(false);
        switch (command.Verb)
        {
            case InstallerMachineHelperVerb.Prepare:
            case InstallerMachineHelperVerb.Remove:
                break;
            case InstallerMachineHelperVerb.CommitPackage:
                packages.Verify(request, release.Manifest, cancellationToken);
                if (disposition == InstallerMachineHelperSessionDisposition.Execute)
                {
                    await certificates.RemoveAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await certificates.VerifyCompletedAsync(cancellationToken).ConfigureAwait(false);
                }
                break;
            case InstallerMachineHelperVerb.Verify:
            case InstallerMachineHelperVerb.Clear:
                packages.Verify(request, release.Manifest, cancellationToken);
                await certificates.VerifyCompletedAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InstallerProtocolException("installer.retired_uninstall.command_invalid");
        }
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        await shared.ReverifyAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Rechecks the retained scope before each archived-certificate observation or mutation.</summary>
internal sealed class WindowsRetiredUninstallCertificateBoundary(
    InstallerRequest request, IInstallerReleaseLease release, IWindowsRetiredUninstallSharedState shared,
    IWindowsTargetUserPackageCommitInspector packages) : IInstallerArchivedCertificateRemovalBoundary
{
    public async Task ReverifyAsync(string authenticatedTargetSid, CancellationToken cancellationToken)
    {
        if (authenticatedTargetSid != request.TargetSid)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.target_sid_mismatch");
        }
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        await shared.ReverifyAsync(cancellationToken).ConfigureAwait(false);
        packages.Verify(request, release.Manifest, cancellationToken);
    }
}
