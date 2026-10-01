using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Packages;

/// <summary>Observes same-identity package content without deploying or modifying Windows state.</summary>
internal interface IWindowsPackageDeploymentPreflight
{
    Task VerifyCanDeployAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken);
}

/// <summary>The catalog queries all users; its roots belong to the exact supplied full package name.</summary>
internal interface IWindowsInstalledPackageFootprintCatalog
{
    IReadOnlyList<string> FindInstalledRoots(string packageFamilyName, string packageFullName, CancellationToken cancellationToken);
}

internal interface IWindowsPackageFootprintReader
{
    WindowsPackageFootprint ReadCandidate(IInstallerReleaseLease release, InstallerPayloadFileEntry entry, CancellationToken cancellationToken);

    WindowsPackageFootprint ReadInstalled(string installedRoot, CancellationToken cancellationToken);
}

internal sealed record WindowsPackageFootprint(long SignatureLength, string SignatureSha256, long BlockMapLength, string BlockMapSha256);

/// <summary>
/// Rejects known primary or dependency same-identity conflicts before service maintenance.
/// Matching signed footprints permit further validation; they do not replace AppXSVC deployment
/// checks, registration postconditions, or the durable recovery protocol.
/// </summary>
internal sealed class WindowsPackageDeploymentPreflight(
    IWindowsInstalledPackageFootprintCatalog catalog,
    IWindowsPackageFootprintReader reader) : IWindowsPackageDeploymentPreflight
{
    private readonly IWindowsInstalledPackageFootprintCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IWindowsPackageFootprintReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async Task VerifyCanDeployAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        request.Validate();
        release.Manifest.Validate();
        release.Release.Validate();
        if (request.Operation is not (InstallerOperation.Install or InstallerOperation.Repair)
            || !release.Manifest.Matches(release.Release)
            || request.ExpectedPackageVersion != release.Release.ExpectedPackageVersion
            || request.InstallerPayloadSha256 != release.Release.InstallerPayloadSha256
            || !release.Release.PackagePayloadAvailable)
        {
            throw new InstallerProtocolException("installer.release.identity_mismatch");
        }

        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            InstallerReleaseManifest manifest = release.Manifest;
            VerifyOne(release, manifest.PackageIdentity.PackageFamilyName, manifest.PackageIdentity.PackageFullName,
                manifest.Files.Single(static entry => entry.Role == InstallerPayloadFileRole.PrimaryPackage), cancellationToken);
            foreach (InstallerDependencyPackageIdentity dependency in manifest.Dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifyOne(release, dependency.PackageFamilyName, dependency.PackageFullName,
                    manifest.Files.Single(entry => entry.Path == dependency.Path), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new InstallerProtocolException("installer.package.content_inspection_failed", exception);
        }
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void VerifyOne(IInstallerReleaseLease release, string familyName, string fullName,
        InstallerPayloadFileEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> roots = _catalog.FindInstalledRoots(familyName, fullName, cancellationToken)
            ?? throw new InstallerProtocolException("installer.package.inspection_result_invalid");
        if (roots.Count == 0)
        {
            return;
        }
        if (roots.Count != 1)
        {
            throw new InstallerProtocolException("installer.package.registration_ambiguous");
        }
        string root = roots[0];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new InstallerProtocolException("installer.package.installed_path_invalid");
        }

        WindowsPackageFootprint candidate = _reader.ReadCandidate(release, entry, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsPackageFootprint installed = _reader.ReadInstalled(root, cancellationToken);
        if (candidate != installed)
        {
            throw new InstallerProtocolException("installer.package.content_conflict");
        }
    }
}
