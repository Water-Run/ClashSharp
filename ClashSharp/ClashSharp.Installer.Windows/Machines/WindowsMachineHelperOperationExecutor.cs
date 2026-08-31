using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Certificates;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperMachineOperations
{
    Task PrepareAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken);

    Task ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken);

    Task RemoveAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken);

    Task VerifyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reconstructs every operation from the authenticated journal, acquires an independent signed
/// release lease, and composes helper-only certificate ownership, target-user certificate,
/// package-observation, and fixed machine/SCM operations. The WPF entry point remains deliberately
/// disconnected until parent/runtime composition and signed Windows VM evidence are complete.
/// </summary>
internal sealed class WindowsMachineHelperOperationExecutor
    : IInstallerMachineHelperOperationExecutor
{
    private readonly IInstallerReleaseVerifier _releaseVerifier;
    private readonly IInstallerCertificateMutation _certificateMutation;
    private readonly IInstallerCertificateMutationVerifier _certificateVerifier;
    private readonly IWindowsTargetUserPackageCommitInspector _packageInspector;
    private readonly IWindowsMachineHelperMachineOperations _machineOperations;

    internal WindowsMachineHelperOperationExecutor(
        IInstallerReleaseVerifier releaseVerifier,
        IInstallerCertificateMutation certificateMutation,
        IInstallerCertificateMutationVerifier certificateVerifier,
        IWindowsTargetUserPackageCommitInspector packageInspector,
        IWindowsMachineHelperMachineOperations machineOperations)
    {
        ArgumentNullException.ThrowIfNull(releaseVerifier);
        ArgumentNullException.ThrowIfNull(certificateMutation);
        ArgumentNullException.ThrowIfNull(certificateVerifier);
        ArgumentNullException.ThrowIfNull(packageInspector);
        ArgumentNullException.ThrowIfNull(machineOperations);
        _releaseVerifier = releaseVerifier;
        _certificateMutation = certificateMutation;
        _certificateVerifier = certificateVerifier;
        _packageInspector = packageInspector;
        _machineOperations = machineOperations;
    }

    internal static WindowsMachineHelperOperationExecutor CreateDefault(
        ReadOnlyMemory<byte> embeddedManifestBytes,
        IInstallerCertificateOwnershipStore certificateOwnershipStore) =>
        CreateDefault(
            embeddedManifestBytes,
            certificateOwnershipStore,
            new WindowsMachineHelperMachineOperations());

    internal static WindowsMachineHelperOperationExecutor CreateDefault(
        ReadOnlyMemory<byte> embeddedManifestBytes,
        IInstallerCertificateOwnershipStore certificateOwnershipStore,
        IWindowsMachineHelperMachineOperations machineOperations) =>
        CreateDefaultCore(
            embeddedManifestBytes,
            certificateOwnershipStore,
            machineOperations);

    public async Task ExecuteAsync(
        InstallerMachineHelperCommand command,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(disposition))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.disposition_invalid");
        }

        InstallerTransactionSnapshot state = command.ToDurableState();
        InstallerRequest request = RequestFrom(state.Journal);
        cancellationToken.ThrowIfCancellationRequested();
        await using IInstallerReleaseLease release = await _releaseVerifier
            .VerifyAsync(request, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InstallerProtocolException(
                "installer.release.lease_missing");
        ValidateRelease(request, release);

        switch (command.Verb)
        {
            case InstallerMachineHelperVerb.Prepare:
                await _machineOperations
                    .PrepareAsync(request, release, disposition, cancellationToken)
                    .ConfigureAwait(false);
                if (request.Operation is InstallerOperation.Install or InstallerOperation.Repair)
                {
                    await ApplyOrVerifyCertificateAsync(
                            request,
                            release,
                            disposition,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                break;
            case InstallerMachineHelperVerb.CommitPackage:
                await VerifyPackageCommitAsync(
                        request,
                        release,
                        disposition,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case InstallerMachineHelperVerb.Apply:
                await _machineOperations
                    .ApplyAsync(request, release, disposition, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case InstallerMachineHelperVerb.Remove:
                await _machineOperations
                    .RemoveAsync(request, release, disposition, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case InstallerMachineHelperVerb.Verify:
            case InstallerMachineHelperVerb.Clear:
                await VerifyFinalStateAsync(request, release, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InstallerProtocolException(
                    "installer.machine_helper.verb_invalid");
        }

        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyPackageCommitAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken)
    {
        _packageInspector.Verify(request, release.Manifest, cancellationToken);
        if (request.Operation is InstallerOperation.Install or InstallerOperation.Repair)
        {
            await _certificateVerifier
                .VerifyAppliedAsync(request, release, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await ApplyOrVerifyCertificateAsync(
                request,
                release,
                disposition,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task VerifyFinalStateAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        await _machineOperations
            .VerifyAsync(request, release, cancellationToken)
            .ConfigureAwait(false);
        _packageInspector.Verify(request, release.Manifest, cancellationToken);
        await _certificateVerifier
            .VerifyAppliedAsync(request, release, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyOrVerifyCertificateAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (disposition == InstallerMachineHelperSessionDisposition.Execute)
        {
            await _certificateMutation
                .ApplyAsync(request, release, cancellationToken)
                .ConfigureAwait(false);
        }

        await _certificateVerifier
            .VerifyAppliedAsync(request, release, cancellationToken)
            .ConfigureAwait(false);
    }

    private static WindowsMachineHelperOperationExecutor CreateDefaultCore(
        ReadOnlyMemory<byte> embeddedManifestBytes,
        IInstallerCertificateOwnershipStore certificateOwnershipStore,
        IWindowsMachineHelperMachineOperations machineOperations)
    {
        ArgumentNullException.ThrowIfNull(certificateOwnershipStore);
        var certificateStore = new WindowsTargetUserCertificateStoreAdapter();
        var certificateMutation = new DurableInstallerCertificateMutation(
            certificateOwnershipStore,
            certificateStore);
        return new WindowsMachineHelperOperationExecutor(
            new WindowsInstallerReleaseVerifier(embeddedManifestBytes),
            certificateMutation,
            certificateMutation,
            new WindowsTargetUserPackageCommitInspector(
                new WindowsPackageManagerFacade()),
            machineOperations);
    }

    private static InstallerRequest RequestFrom(InstallerTransactionJournal journal)
    {
        journal.Validate();
        var request = new InstallerRequest(
            journal.Operation,
            journal.TargetSid,
            journal.AllowReassociation,
            journal.ExpectedPackageVersion,
            journal.InstallerPayloadSha256);
        request.Validate();
        return request;
    }

    private static void ValidateRelease(
        InstallerRequest request,
        IInstallerReleaseLease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        release.Release.Validate();
        release.Manifest.Validate();
        if (!release.Manifest.Matches(release.Release)
            || !string.Equals(
                request.ExpectedPackageVersion,
                release.Release.ExpectedPackageVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                request.InstallerPayloadSha256,
                release.Release.InstallerPayloadSha256,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.release.identity_mismatch");
        }
    }

}
