using System.Text.Json.Serialization;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Payloads;

/// <summary>Exact payload trust anchor embedded in the signed installer executable.</summary>
public sealed class InstallerReleaseManifest
{
    private static readonly HashSet<string> RequiredMachineFilePaths = new(StringComparer.Ordinal)
    {
        "binaries/geodata/asn.mmdb",
        "binaries/geodata/country.mmdb",
        "binaries/geodata/geoip.dat",
        "binaries/geodata/geosite.dat",
        "binaries/geodata/manifest.json",
        "binaries/mihomo.exe",
        "binaries/service/clashsharp.mihomoservice.exe",
    };

    /// <summary>The only currently supported manifest schema.</summary>
    public const int CurrentSchema = 2;

    /// <summary>Creates an immutable manifest and snapshots its file entries.</summary>
    [JsonConstructor]
    public InstallerReleaseManifest(
        int schema,
        string expectedPackageVersion,
        string installerPayloadSha256,
        string authenticodeCertificateThumbprint,
        string packageCertificateThumbprint,
        string certificateSha256,
        InstallerPackageIdentity packageIdentity,
        IReadOnlyList<InstallerDependencyPackageIdentity> dependencies,
        IReadOnlyList<InstallerMachinePayloadFileEntry> machineFiles,
        IReadOnlyList<InstallerPayloadFileEntry> files)
    {
        Schema = schema;
        ExpectedPackageVersion = expectedPackageVersion;
        InstallerPayloadSha256 = installerPayloadSha256;
        AuthenticodeCertificateThumbprint = authenticodeCertificateThumbprint;
        PackageCertificateThumbprint = packageCertificateThumbprint;
        CertificateSha256 = certificateSha256;
        PackageIdentity = packageIdentity;
        Dependencies = dependencies?.ToArray()!;
        MachineFiles = machineFiles?.ToArray()!;
        Files = files?.ToArray()!;
    }

    /// <summary>Gets the manifest schema.</summary>
    public int Schema { get; }

    /// <summary>Gets the canonical four-component primary package version.</summary>
    public string ExpectedPackageVersion { get; }

    /// <summary>Gets the exact primary MSIX SHA-256 bound into transactions.</summary>
    public string InstallerPayloadSha256 { get; }

    /// <summary>Gets the uppercase SHA-1 identity of the signed Installer executable publisher.</summary>
    public string AuthenticodeCertificateThumbprint { get; }

    /// <summary>Gets the uppercase SHA-1 identity of the package certificate.</summary>
    public string PackageCertificateThumbprint { get; }

    /// <summary>Gets the lowercase SHA-256 of the complete DER certificate.</summary>
    public string CertificateSha256 { get; }

    /// <summary>Gets the exact primary package and application identity.</summary>
    public InstallerPackageIdentity PackageIdentity { get; }

    /// <summary>Gets the path-sorted exact identity of every dependency MSIX.</summary>
    public IReadOnlyList<InstallerDependencyPackageIdentity> Dependencies { get; }

    /// <summary>
    /// Gets the exact, canonical, path-sorted machine payload contained in the primary MSIX.
    /// </summary>
    public IReadOnlyList<InstallerMachinePayloadFileEntry> MachineFiles { get; }

    /// <summary>Gets the exact, canonical, path-sorted sibling file set.</summary>
    public IReadOnlyList<InstallerPayloadFileEntry> Files { get; }

    /// <summary>Validates identity binding, exact roles, order, path set, and budgets.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema)
        {
            throw new InstallerProtocolException("installer.release.manifest_schema_invalid");
        }

        InstallerProtocolValidation.ValidatePackageVersion(ExpectedPackageVersion);
        InstallerProtocolValidation.ValidateLowerHex256(
            InstallerPayloadSha256,
            "installer.release.payload_hash_invalid");
        InstallerProtocolValidation.ValidateUpperHex160(
            AuthenticodeCertificateThumbprint,
            "installer.release.authenticode_thumbprint_invalid");
        InstallerProtocolValidation.ValidateUpperHex160(
            PackageCertificateThumbprint,
            "installer.release.certificate_thumbprint_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(
            CertificateSha256,
            "installer.release.certificate_hash_invalid");
        if (PackageIdentity is null)
        {
            throw new InstallerProtocolException("installer.release.package_identity_invalid");
        }

        PackageIdentity.Validate(ExpectedPackageVersion);

        ValidateMachineFiles();

        if (Files is null || Files.Count is < 4 or > InstallerPayloadBudgets.MaximumFileCount)
        {
            throw new InstallerProtocolException("installer.release.payload_file_budget_exceeded");
        }

        var observedPaths = new HashSet<string>(StringComparer.Ordinal);
        long totalLength = 0;
        string? previousPath = null;
        foreach (InstallerPayloadFileEntry? file in Files)
        {
            if (file is null)
            {
                throw new InstallerProtocolException("installer.release.manifest_file_invalid");
            }

            file.Validate();
            if (!observedPaths.Add(file.Path))
            {
                throw new InstallerProtocolException("installer.release.payload_file_set_invalid");
            }

            if (previousPath is not null
                && string.CompareOrdinal(previousPath, file.Path) >= 0)
            {
                throw new InstallerProtocolException("installer.release.manifest_file_order_invalid");
            }

            previousPath = file.Path;
            try
            {
                totalLength = checked(totalLength + file.Length);
            }
            catch (OverflowException exception)
            {
                throw new InstallerProtocolException(
                    "installer.release.payload_size_budget_exceeded",
                    exception);
            }

            if (totalLength > InstallerPayloadBudgets.MaximumPayloadBytes)
            {
                throw new InstallerProtocolException(
                    "installer.release.payload_size_budget_exceeded");
            }
        }

        InstallerPayloadFileEntry[] primary = Files
            .Where(static file => file.Role == InstallerPayloadFileRole.PrimaryPackage)
            .ToArray();
        InstallerPayloadFileEntry[] certificates = Files
            .Where(static file => file.Role == InstallerPayloadFileRole.Certificate)
            .ToArray();
        InstallerPayloadFileEntry[] provenance = Files
            .Where(static file => file.Role == InstallerPayloadFileRole.Provenance)
            .ToArray();
        int dependencies = Files.Count(static file =>
            file.Role == InstallerPayloadFileRole.DependencyPackage);
        if (primary.Length != 1
            || certificates.Length != 1
            || provenance.Length != 1
            || dependencies < 1)
        {
            throw new InstallerProtocolException("installer.release.payload_file_set_invalid");
        }

        if (!string.Equals(primary[0].Sha256, InstallerPayloadSha256, StringComparison.Ordinal)
            || !string.Equals(certificates[0].Sha256, CertificateSha256, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.release.manifest_identity_mismatch");
        }

        InstallerPayloadFileEntry[] dependencyFiles = Files
            .Where(static file => file.Role == InstallerPayloadFileRole.DependencyPackage)
            .ToArray();
        if (Dependencies is null || Dependencies.Count != dependencyFiles.Length)
        {
            throw new InstallerProtocolException(
                "installer.release.dependency_identity_set_invalid");
        }

        string? previousDependencyPath = null;
        var dependencyFamilies = new HashSet<string>(StringComparer.Ordinal);
        var dependencyFullNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Dependencies.Count; index++)
        {
            InstallerDependencyPackageIdentity? dependency = Dependencies[index];
            if (dependency is null)
            {
                throw new InstallerProtocolException(
                    "installer.release.dependency_identity_set_invalid");
            }

            dependency.Validate();
            if (!string.Equals(dependency.Path, dependencyFiles[index].Path, StringComparison.Ordinal)
                || !dependencyFamilies.Add(dependency.PackageFamilyName)
                || !dependencyFullNames.Add(dependency.PackageFullName)
                || (previousDependencyPath is not null
                    && string.CompareOrdinal(previousDependencyPath, dependency.Path) >= 0))
            {
                throw new InstallerProtocolException(
                    "installer.release.dependency_identity_set_invalid");
            }

            previousDependencyPath = dependency.Path;
        }

        HashSet<string> directories = Files
            .SelectMany(static file => ParentDirectories(file.Path))
            .ToHashSet(StringComparer.Ordinal);
        if (directories.Count > InstallerPayloadBudgets.MaximumDirectoryCount
            || !directories.SetEquals(["dependencies", "dependencies/x64"]))
        {
            throw new InstallerProtocolException("installer.release.payload_directory_set_invalid");
        }
    }

    /// <summary>Creates the release identity derived only from this validated manifest.</summary>
    public VerifiedInstallerRelease CreateVerifiedRelease(
        bool packagePayloadAvailable,
        bool certificatePayloadAvailable)
    {
        Validate();
        return new VerifiedInstallerRelease(
            ExpectedPackageVersion,
            InstallerPayloadSha256,
            packagePayloadAvailable,
            PackageCertificateThumbprint,
            CertificateSha256,
            certificatePayloadAvailable);
    }

    /// <summary>Checks whether a verified release was derived from this manifest.</summary>
    public bool Matches(VerifiedInstallerRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return string.Equals(
                ExpectedPackageVersion,
                release.ExpectedPackageVersion,
                StringComparison.Ordinal)
            && string.Equals(
                InstallerPayloadSha256,
                release.InstallerPayloadSha256,
                StringComparison.Ordinal)
            && string.Equals(
                PackageCertificateThumbprint,
                release.PackageCertificateThumbprint,
                StringComparison.Ordinal)
            && string.Equals(
                CertificateSha256,
                release.CertificateSha256,
                StringComparison.Ordinal);
    }

    private static IEnumerable<string> ParentDirectories(string path)
    {
        int separator = path.IndexOf('/');
        while (separator >= 0)
        {
            yield return path[..separator];
            separator = path.IndexOf('/', separator + 1);
        }
    }

    private void ValidateMachineFiles()
    {
        if (MachineFiles is null
            || MachineFiles.Count != InstallerPayloadBudgets.MachinePayloadFileCount)
        {
            throw new InstallerProtocolException(
                "installer.release.machine_file_set_invalid");
        }

        var observedPaths = new HashSet<string>(StringComparer.Ordinal);
        long totalLength = 0;
        string? previousPath = null;
        foreach (InstallerMachinePayloadFileEntry? file in MachineFiles)
        {
            if (file is null)
            {
                throw new InstallerProtocolException(
                    "installer.release.machine_file_invalid");
            }

            file.Validate();
            if (!observedPaths.Add(file.Path))
            {
                throw new InstallerProtocolException(
                    "installer.release.machine_file_set_invalid");
            }

            if (previousPath is not null
                && string.CompareOrdinal(previousPath, file.Path) >= 0)
            {
                throw new InstallerProtocolException(
                    "installer.release.machine_file_order_invalid");
            }

            previousPath = file.Path;
            try
            {
                totalLength = checked(totalLength + file.Length);
            }
            catch (OverflowException exception)
            {
                throw new InstallerProtocolException(
                    "installer.release.machine_payload_size_budget_exceeded",
                    exception);
            }

            if (totalLength > InstallerPayloadBudgets.MaximumMachinePayloadBytes)
            {
                throw new InstallerProtocolException(
                    "installer.release.machine_payload_size_budget_exceeded");
            }
        }

        if (!observedPaths.SetEquals(RequiredMachineFilePaths))
        {
            throw new InstallerProtocolException(
                "installer.release.machine_file_set_invalid");
        }
    }
}
