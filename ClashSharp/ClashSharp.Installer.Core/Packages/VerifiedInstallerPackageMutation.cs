using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Packages;

/// <summary>
/// Applies idempotent current-user package mutations and independently verifies their result.
/// </summary>
public sealed class VerifiedInstallerPackageMutation : IInstallerPackageMutation
{
    private readonly IInstallerPackageStoreAdapter _packageStore;

    /// <summary>Initializes package mutation over an explicit platform store adapter.</summary>
    public VerifiedInstallerPackageMutation(IInstallerPackageStoreAdapter packageStore)
    {
        ArgumentNullException.ThrowIfNull(packageStore);
        _packageStore = packageStore;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ValidateBoundary(request, release);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Operation == InstallerOperation.Uninstall)
        {
            await RemoveAsync(request, release, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DeployAsync(request, release, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeployAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        InstallerInstalledPackage? installed = await InspectValidAsync(
                request,
                release,
                cancellationToken)
            .ConfigureAwait(false);
        Version requestedVersion = InstallerProtocolValidation.ParsePackageVersion(
            request.ExpectedPackageVersion);
        if (installed is not null)
        {
            Version installedVersion = InstallerProtocolValidation.ParsePackageVersion(
                installed.Version);
            if (installedVersion > requestedVersion)
            {
                throw new InstallerProtocolException("installer.package.downgrade_rejected");
            }

            // Install is also the upgrade entry point. An exact target means a previous deploy
            // completed before the durable phase advanced, so replay is already satisfied.
            if (request.Operation == InstallerOperation.Install
                && installedVersion == requestedVersion
                && installed.IsHealthy)
            {
                return;
            }
        }
        else if (request.Operation == InstallerOperation.Repair)
        {
            throw new InstallerProtocolException(
                "installer.package.repair_requires_installation");
        }

        if (!release.Release.PackagePayloadAvailable)
        {
            throw new InstallerProtocolException("installer.release.package_payload_missing");
        }

        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        await _packageStore.DeployAsync(request, release, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        InstallerInstalledPackage? committed = await InspectValidAsync(
                request,
                release,
                cancellationToken)
            .ConfigureAwait(false);
        if (committed is null
            || !committed.IsHealthy
            || !string.Equals(
                committed.Version,
                request.ExpectedPackageVersion,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.package.deployment_verification_failed");
        }
    }

    private async Task RemoveAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        InstallerInstalledPackage? installed = await InspectValidAsync(
                request,
                release,
                cancellationToken)
            .ConfigureAwait(false);
        if (installed is null)
        {
            return;
        }

        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        await _packageStore
            .RemoveAsync(request, release, installed, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (await InspectValidAsync(request, release, cancellationToken).ConfigureAwait(false)
            is not null)
        {
            throw new InstallerProtocolException(
                "installer.package.removal_verification_failed");
        }
    }

    private async Task<InstallerInstalledPackage?> InspectValidAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        InstallerInstalledPackage? installed = await _packageStore
            .InspectAsync(request, release, cancellationToken)
            .ConfigureAwait(false);
        if (installed is not null && !installed.MatchesReleaseFamily(release.Manifest))
        {
            throw new InstallerProtocolException(
                "installer.package.installed_identity_mismatch");
        }

        return installed;
    }

    private static void ValidateBoundary(
        InstallerRequest request,
        IInstallerReleaseLease release)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
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
            throw new InstallerProtocolException("installer.release.identity_mismatch");
        }
    }
}
