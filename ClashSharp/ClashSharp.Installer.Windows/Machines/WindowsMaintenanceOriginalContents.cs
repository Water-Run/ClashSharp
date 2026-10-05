using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Machines;

internal sealed record WindowsMaintenanceFileFingerprint(string PackagePath, string TargetPath, long Length, string Sha256);

internal sealed record WindowsMaintenanceTrustFingerprint(string UserLedgerSha256, string MachineLedgerSha256,
    string CertificateThumbprint, string CertificateSha256);

/// <summary>
/// Original installation observations excluding service state, which preparation intentionally
/// changes. All paths are plan-derived relative paths; no observed path may authorize restoration.
/// Package registration is target-user specific even when package files are globally shared.
/// </summary>
internal sealed class WindowsMaintenanceOriginalContents
{
    public WindowsMaintenanceOriginalContents(InstallerInstalledPackage package, WindowsPackageFootprint footprint, string packageContentsSha256,
        IReadOnlyList<WindowsMaintenanceFileFingerprint> files, string associationSha256, WindowsMaintenanceTrustFingerprint trust)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Footprint = footprint ?? throw new ArgumentNullException(nameof(footprint));
        PackageContentsSha256 = packageContentsSha256;
        ArgumentNullException.ThrowIfNull(files);
        Files = Array.AsReadOnly(files.ToArray());
        AssociationSha256 = associationSha256;
        Trust = trust ?? throw new ArgumentNullException(nameof(trust));
        Validate();
    }

    public InstallerInstalledPackage Package { get; }
    public WindowsPackageFootprint Footprint { get; }
    public string PackageContentsSha256 { get; }
    public IReadOnlyList<WindowsMaintenanceFileFingerprint> Files { get; }
    public string AssociationSha256 { get; }
    public WindowsMaintenanceTrustFingerprint Trust { get; }

    internal void RequirePlan(WindowsMachineDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        Validate();
        if (!Package.MatchesReleaseFamily(plan.Manifest) || Files.Count != plan.PayloadTargets.Count
            || Trust.CertificateThumbprint != plan.Manifest.PackageCertificateThumbprint
            || Trust.CertificateSha256 != plan.Manifest.CertificateSha256)
        {
            throw new InstallerProtocolException("installer.recovery.original_contents_mismatch");
        }
        for (int index = 0; index < Files.Count; index++)
        {
            WindowsMachinePayloadTarget target = plan.PayloadTargets[index];
            WindowsMaintenanceFileFingerprint file = Files[index];
            if (file.PackagePath != target.Source.Path || file.TargetPath != target.RelativeTargetPath.Replace('\\', '/'))
            {
                throw new InstallerProtocolException("installer.recovery.original_contents_mismatch");
            }
        }
    }

    internal bool Matches(WindowsMaintenanceOriginalContents other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Validate();
        other.Validate();
        return Package == other.Package && Footprint == other.Footprint && PackageContentsSha256 == other.PackageContentsSha256 && Files.SequenceEqual(other.Files)
            && AssociationSha256 == other.AssociationSha256 && Trust == other.Trust;
    }

    private void Validate()
    {
        Package.Validate();
        if (!Package.IsHealthy || Files.Count is < 1 or > InstallerPayloadBudgets.MaximumFileCount
            || Footprint.SignatureLength is < 1 or > WindowsPackageFootprintReader.MaximumSignatureBytes
            || Footprint.BlockMapLength is < 1 or > WindowsPackageFootprintReader.MaximumBlockMapBytes)
        {
            throw new InstallerProtocolException("installer.recovery.original_contents_invalid");
        }
        Hash(Footprint.SignatureSha256);
        Hash(PackageContentsSha256);
        Hash(Footprint.BlockMapSha256);
        Hash(AssociationSha256);
        Hash(Trust.UserLedgerSha256);
        Hash(Trust.MachineLedgerSha256);
        Hash(Trust.CertificateSha256);
        InstallerProtocolValidation.ValidateUpperHex160(Trust.CertificateThumbprint, "installer.recovery.original_contents_invalid");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (WindowsMaintenanceFileFingerprint? file in Files)
        {
            if (file is null || file.Length <= 0 || !paths.Add(file.PackagePath))
            {
                throw new InstallerProtocolException("installer.recovery.original_contents_invalid");
            }
            new InstallerMachinePayloadFileEntry(file.PackagePath, file.Length, file.Sha256).Validate();
            if (string.IsNullOrEmpty(file.TargetPath) || Path.IsPathRooted(file.TargetPath) || file.TargetPath.Contains('\\', StringComparison.Ordinal)
                || file.TargetPath.Split('/').Any(part => part is "" or "." or ".."))
            {
                throw new InstallerProtocolException("installer.recovery.original_contents_invalid");
            }
        }
    }

    private static void Hash(string value) => InstallerProtocolValidation.ValidateLowerHex256(value, "installer.recovery.original_contents_invalid");
}
