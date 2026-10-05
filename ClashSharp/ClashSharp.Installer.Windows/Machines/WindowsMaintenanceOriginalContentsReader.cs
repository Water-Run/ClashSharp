using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMaintenancePayloadStateReader
{
    IReadOnlyList<WindowsMaintenanceFileFingerprint> Read(WindowsMachineDeploymentPlan plan, string originalPackageRoot, CancellationToken cancellationToken);
}

internal interface IWindowsMaintenancePackageStateReader
{
    string Read(string originalPackageRoot, CancellationToken cancellationToken);
}

/// <summary>
/// Observes the target user's healthy original registration, exact globally shared package root,
/// package/payload bytes, association and trust. The recovery authority keeps original file/path
/// leases alive across these reads and later SCM effects; this reader grants no mutation authority.
/// </summary>
internal sealed class WindowsMaintenanceOriginalContentsReader : IWindowsMaintenanceOriginalContentsReader
{
    private readonly IWindowsPackageManagerFacade _packages;
    private readonly IWindowsInstalledPackageFootprintCatalog _roots;
    private readonly IWindowsPackageFootprintReader _footprints;
    private readonly IWindowsMaintenancePayloadStateReader _payload;
    private readonly IWindowsMaintenancePackageStateReader _packageContents;
    private readonly IWindowsMachineAssociationStore _association;
    private readonly IWindowsMaintenanceTrustStateReader _trust;
    private readonly IInstallerReleaseLease _release;

    internal WindowsMaintenanceOriginalContentsReader(IWindowsPackageManagerFacade packages,
        IWindowsInstalledPackageFootprintCatalog roots, IWindowsPackageFootprintReader footprints,
        IWindowsMaintenancePayloadStateReader payload, IWindowsMaintenancePackageStateReader packageContents, IWindowsMachineAssociationStore association,
        IWindowsMaintenanceTrustStateReader trust, IInstallerReleaseLease release)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _footprints = footprints ?? throw new ArgumentNullException(nameof(footprints));
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _packageContents = packageContents ?? throw new ArgumentNullException(nameof(packageContents));
        _association = association ?? throw new ArgumentNullException(nameof(association));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public async Task<WindowsMaintenanceOriginalContents> ReadAsync(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Request.Operation != InstallerOperation.Repair
            || !ReferenceEquals(plan.Manifest, _release.Manifest)
            || !plan.Manifest.Matches(_release.Release) || !_release.Manifest.Matches(_release.Release))
        {
            throw new InstallerProtocolException("installer.recovery.original_contents_mismatch");
        }
        await _release.ReverifyAsync(plan.Request, cancellationToken).ConfigureAwait(false);
        InstallerInstalledPackage before = ReadPackage(plan);
        IReadOnlyList<string> roots = _roots.FindInstalledRoots(before.PackageFamilyName, before.PackageFullName, cancellationToken);
        if (roots is null || roots.Count != 1 || string.IsNullOrEmpty(roots[0]) || !Path.IsPathFullyQualified(roots[0]))
        {
            throw new InstallerProtocolException("installer.recovery.original_package_root_invalid");
        }
        WindowsPackageFootprint footprint = _footprints.ReadInstalled(roots[0], cancellationToken);
        string packageContentsHash = _packageContents.Read(roots[0], cancellationToken);
        IReadOnlyList<WindowsMaintenanceFileFingerprint> files = _payload.Read(plan, roots[0], cancellationToken);
        InstallerMachineAssociationObservation association = await _association.InspectAsync(cancellationToken).ConfigureAwait(false);
        association.Validate();
        if (association.Status != InstallerMachineAssociationStatus.Valid || association.Association != plan.Association)
        {
            throw new InstallerProtocolException("installer.recovery.original_association_changed");
        }
        byte[] associationBytes = InstallerMachineAssociationCodec.Serialize(plan.Association);
        try
        {
            WindowsMaintenanceTrustFingerprint trust = await _trust.ReadAsync(plan.Request, _release, cancellationToken).ConfigureAwait(false);
            InstallerInstalledPackage after = ReadPackage(plan);
            if (after != before) { throw new InstallerProtocolException("installer.recovery.original_package_changed"); }
            await _release.ReverifyAsync(plan.Request, cancellationToken).ConfigureAwait(false);
            var contents = new WindowsMaintenanceOriginalContents(before, footprint, packageContentsHash, files,
                Convert.ToHexStringLower(SHA256.HashData(associationBytes)), trust);
            contents.RequirePlan(plan);
            return contents;
        }
        finally { CryptographicOperations.ZeroMemory(associationBytes); }
    }

    private InstallerInstalledPackage ReadPackage(WindowsMachineDeploymentPlan plan)
    {
        InstallerInstalledPackage package = WindowsPackageRegistrationInspector.Inspect(_packages, plan.Request.TargetSid, plan.Manifest)
            ?? throw new InstallerProtocolException("installer.recovery.original_package_missing");
        if (!package.IsHealthy) { throw new InstallerProtocolException("installer.recovery.original_package_unhealthy"); }
        return package;
    }
}
