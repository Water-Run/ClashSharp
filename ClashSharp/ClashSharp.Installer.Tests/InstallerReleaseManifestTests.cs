using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerReleaseManifestTests
{
    [Fact]
    public void CanonicalManifestBindsExactReleaseIdentity()
    {
        InstallerReleaseManifest manifest = InstallerTestData.Manifest();

        manifest.Validate();
        VerifiedInstallerRelease release = manifest.CreateVerifiedRelease(
            packagePayloadAvailable: true,
            certificatePayloadAvailable: true);

        Assert.True(manifest.Matches(release));
        Assert.Equal(InstallerTestData.Hash, release.InstallerPayloadSha256);
        Assert.Equal(InstallerTestData.CertificateHash, release.CertificateSha256);
    }

    [Fact]
    public void UnsupportedSchemaIsRejected()
    {
        InstallerReleaseManifest manifest = CreateManifest(
            InstallerTestData.Manifest().Files,
            schema: InstallerReleaseManifest.CurrentSchema + 1);

        AssertDiagnostic(
            manifest.Validate,
            "installer.release.manifest_schema_invalid");
    }

    [Fact]
    public void PrimaryAndCertificateHashesMustBindManifestIdentity()
    {
        InstallerPayloadFileEntry[] wrongPrimary = ReplaceFile(
            InstallerPayloadFileRole.PrimaryPackage,
            static entry => entry with { Sha256 = InstallerTestData.OtherHash });
        AssertDiagnostic(
            () => CreateManifest(wrongPrimary).Validate(),
            "installer.release.manifest_identity_mismatch");

        InstallerPayloadFileEntry[] wrongCertificate = ReplaceFile(
            InstallerPayloadFileRole.Certificate,
            static entry => entry with { Sha256 = InstallerTestData.OtherHash });
        AssertDiagnostic(
            () => CreateManifest(wrongCertificate).Validate(),
            "installer.release.manifest_identity_mismatch");
    }

    [Fact]
    public void ExactRolesRequireOnePrimaryCertificateProvenanceAndDependency()
    {
        InstallerPayloadFileEntry[] duplicatePrimary = ReplaceFile(
            InstallerPayloadFileRole.DependencyPackage,
            static entry => entry with
            {
                Path = "other.msix",
                Role = InstallerPayloadFileRole.PrimaryPackage,
            });

        AssertDiagnostic(
            () => CreateManifest(duplicatePrimary).Validate(),
            "installer.release.payload_file_set_invalid");

        InstallerPayloadFileEntry[] noDependency = InstallerTestData.Manifest().Files
            .Where(static entry => entry.Role != InstallerPayloadFileRole.DependencyPackage)
            .ToArray();
        AssertDiagnostic(
            () => CreateManifest(noDependency).Validate(),
            "installer.release.payload_file_budget_exceeded");
    }

    [Fact]
    public void DuplicateAndNonAscendingPathsAreRejected()
    {
        InstallerPayloadFileEntry[] duplicate = InstallerTestData.Manifest().Files.ToArray();
        duplicate[2] = duplicate[1] with { Role = InstallerPayloadFileRole.Certificate };
        AssertDiagnostic(
            () => CreateManifest(duplicate).Validate(),
            "installer.release.payload_file_set_invalid");

        InstallerPayloadFileEntry[] reversed = InstallerTestData.Manifest().Files
            .Reverse()
            .ToArray();
        AssertDiagnostic(
            () => CreateManifest(reversed).Validate(),
            "installer.release.manifest_file_order_invalid");
    }

    [Fact]
    public void MachinePayloadRequiresTheExactPathSetAndOrdinalOrder()
    {
        InstallerMachinePayloadFileEntry[] canonical = InstallerTestData.MachineFiles();
        Assert.Equal(InstallerPayloadBudgets.MachinePayloadFileCount, canonical.Length);
        CreateManifest(InstallerTestData.Manifest().Files, machineFiles: canonical).Validate();

        AssertDiagnostic(
            () => CreateManifest(
                InstallerTestData.Manifest().Files,
                machineFiles: canonical[..^1]).Validate(),
            "installer.release.machine_file_set_invalid");

        InstallerMachinePayloadFileEntry[] unexpected = canonical.ToArray();
        unexpected[^1] = unexpected[^1] with
        {
            Path = "binaries/service/other.exe",
        };
        AssertDiagnostic(
            () => CreateManifest(
                InstallerTestData.Manifest().Files,
                machineFiles: unexpected).Validate(),
            "installer.release.machine_file_set_invalid");

        AssertDiagnostic(
            () => CreateManifest(
                InstallerTestData.Manifest().Files,
                machineFiles: canonical.Reverse().ToArray()).Validate(),
            "installer.release.machine_file_order_invalid");
    }

    [Theory]
    [InlineData("Binaries/mihomo.exe")]
    [InlineData("binaries\\mihomo.exe")]
    [InlineData("binaries/service/../mihomo.exe")]
    [InlineData("binaries/service/con.exe")]
    [InlineData("/binaries/mihomo.exe")]
    public void MachinePayloadPathsMustBeCanonicalAndWindowsUnambiguous(string path)
    {
        InstallerMachinePayloadFileEntry entry =
            InstallerTestData.MachineFiles()[^2] with { Path = path };

        AssertDiagnostic(
            entry.Validate,
            "installer.release.manifest_path_invalid");
    }

    [Fact]
    public void MachinePayloadRoleBudgetsAndHashesAreStrict()
    {
        InstallerMachinePayloadFileEntry geodataManifest =
            InstallerTestData.MachineFiles().Single(static entry =>
                entry.Path == "binaries/geodata/manifest.json") with
            {
                Length = InstallerPayloadBudgets.MaximumGeoDataManifestBytes + 1,
            };
        AssertDiagnostic(
            geodataManifest.Validate,
            "installer.release.machine_file_size_invalid");

        InstallerMachinePayloadFileEntry geodataAsset =
            InstallerTestData.MachineFiles()[0] with
            {
                Length = InstallerPayloadBudgets.MaximumGeoDataAssetBytes + 1,
            };
        AssertDiagnostic(
            geodataAsset.Validate,
            "installer.release.machine_file_size_invalid");

        InstallerMachinePayloadFileEntry executable =
            InstallerTestData.MachineFiles()[^2] with
            {
                Length = InstallerPayloadBudgets.MaximumFileBytes + 1,
            };
        AssertDiagnostic(
            executable.Validate,
            "installer.release.machine_file_size_invalid");

        InstallerMachinePayloadFileEntry uppercaseHash =
            InstallerTestData.MachineFiles()[^2] with
            {
                Sha256 = InstallerTestData.Hash.ToUpperInvariant(),
            };
        AssertDiagnostic(
            uppercaseHash.Validate,
            "installer.release.machine_file_hash_invalid");
    }

    [Fact]
    public void MachinePayloadCombinedBudgetAcceptsTheBoundaryAndRejectsOneByteMore()
    {
        InstallerMachinePayloadFileEntry[] exact = InstallerTestData.MachineFiles()
            .Select(static entry => entry with
            {
                Length = entry.Path switch
                {
                    "binaries/mihomo.exe" => InstallerPayloadBudgets.MaximumFileBytes - 5,
                    "binaries/service/clashsharp.mihomoservice.exe" =>
                        InstallerPayloadBudgets.MaximumFileBytes,
                    _ => 1,
                },
            })
            .ToArray();
        CreateManifest(InstallerTestData.Manifest().Files, machineFiles: exact).Validate();

        InstallerMachinePayloadFileEntry[] over = exact
            .Select(static entry => entry.Path == "binaries/mihomo.exe"
                ? entry with { Length = entry.Length + 1 }
                : entry)
            .ToArray();
        AssertDiagnostic(
            () => CreateManifest(
                InstallerTestData.Manifest().Files,
                machineFiles: over).Validate(),
            "installer.release.machine_payload_size_budget_exceeded");
    }

    [Theory]
    [InlineData("Dependencies/x64/framework.msix")]
    [InlineData("dependencies\\x64\\framework.msix")]
    [InlineData("dependencies/x64/../framework.msix")]
    [InlineData("dependencies/x64/con.msix")]
    [InlineData("dependencies/x64/framework.msix.")]
    [InlineData("dependencies/x64/c:framework.msix")]
    [InlineData("/dependencies/x64/framework.msix")]
    [InlineData("dependencies//x64/framework.msix")]
    public void NoncanonicalOrWindowsAmbiguousPathsAreRejected(string path)
    {
        InstallerPayloadFileEntry entry = new(
            path,
            InstallerPayloadFileRole.DependencyPackage,
            1,
            InstallerTestData.DependencyHash);

        Assert.Throws<InstallerProtocolException>(entry.Validate);
    }

    [Fact]
    public void RoleSpecificPathsAndLengthsAreEnforced()
    {
        InstallerPayloadFileEntry wrongCertificate = new(
            "other.cer",
            InstallerPayloadFileRole.Certificate,
            1,
            InstallerTestData.CertificateHash);
        AssertDiagnostic(
            wrongCertificate.Validate,
            "installer.release.manifest_file_invalid");

        InstallerPayloadFileEntry oversizedCertificate = new(
            "clashsharp_temporarykey.cer",
            InstallerPayloadFileRole.Certificate,
            InstallerPayloadBudgets.MaximumCertificateBytes + 1,
            InstallerTestData.CertificateHash);
        AssertDiagnostic(
            oversizedCertificate.Validate,
            "installer.release.manifest_file_invalid");

        InstallerPayloadFileEntry oversizedProvenance = new(
            "payload-provenance.json",
            InstallerPayloadFileRole.Provenance,
            InstallerPayloadBudgets.MaximumProvenanceBytes + 1,
            InstallerTestData.OtherHash);
        AssertDiagnostic(
            oversizedProvenance.Validate,
            "installer.release.manifest_file_invalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveFileLengthsAreRejected(long length)
    {
        InstallerPayloadFileEntry entry = InstallerTestData.Manifest().Files[0] with { Length = length };
        AssertDiagnostic(
            entry.Validate,
            "installer.release.payload_file_size_invalid");
    }

    [Fact]
    public void PerFileBudgetAcceptsExactLimitAndRejectsLimitPlusOne()
    {
        InstallerPayloadFileEntry[] exact = ReplaceFile(
            InstallerPayloadFileRole.PrimaryPackage,
            static entry => entry with { Length = InstallerPayloadBudgets.MaximumFileBytes });
        CreateManifest(exact).Validate();

        InstallerPayloadFileEntry over = exact[0] with
        {
            Length = InstallerPayloadBudgets.MaximumFileBytes + 1,
        };
        AssertDiagnostic(
            over.Validate,
            "installer.release.payload_file_size_invalid");
    }

    [Fact]
    public void TotalPayloadBudgetAcceptsExactLimitAndRejectsLimitPlusOne()
    {
        InstallerPayloadFileEntry[] exact = CreateBudgetFiles(
            InstallerPayloadBudgets.MaximumFileBytes - 2,
            InstallerPayloadBudgets.MaximumFileBytes,
            certificateLength: 1,
            provenanceLength: 1);
        CreateManifest(exact).Validate();

        InstallerPayloadFileEntry[] over = CreateBudgetFiles(
            InstallerPayloadBudgets.MaximumFileBytes - 1,
            InstallerPayloadBudgets.MaximumFileBytes,
            certificateLength: 1,
            provenanceLength: 1);
        AssertDiagnostic(
            () => CreateManifest(over).Validate(),
            "installer.release.payload_size_budget_exceeded");
    }

    [Fact]
    public void FileCountBudgetAcceptsExactLimitAndRejectsLimitPlusOne()
    {
        InstallerPayloadFileEntry[] exact = CreateFileCountManifest(
            InstallerPayloadBudgets.MaximumFileCount);
        CreateManifest(exact).Validate();

        InstallerPayloadFileEntry[] over = CreateFileCountManifest(
            InstallerPayloadBudgets.MaximumFileCount + 1);
        AssertDiagnostic(
            () => CreateManifest(over).Validate(),
            "installer.release.payload_file_budget_exceeded");
    }

    [Fact]
    public void UnknownRoleAndNoncanonicalHashAreRejected()
    {
        InstallerPayloadFileEntry unknownRole = InstallerTestData.Manifest().Files[0] with
        {
            Role = (InstallerPayloadFileRole)999,
        };
        AssertDiagnostic(
            unknownRole.Validate,
            "installer.release.manifest_file_invalid");

        InstallerPayloadFileEntry uppercaseHash = InstallerTestData.Manifest().Files[0] with
        {
            Sha256 = InstallerTestData.Hash.ToUpperInvariant(),
        };
        AssertDiagnostic(
            uppercaseHash.Validate,
            "installer.release.manifest_file_hash_invalid");
    }

    private static InstallerReleaseManifest CreateManifest(
        IReadOnlyList<InstallerPayloadFileEntry> files,
        int schema = InstallerReleaseManifest.CurrentSchema,
        IReadOnlyList<InstallerMachinePayloadFileEntry>? machineFiles = null) =>
        new(
            schema,
            InstallerTestData.Version,
            InstallerTestData.Hash,
            InstallerTestData.CertificateThumbprint,
            InstallerTestData.CertificateHash,
            InstallerTestData.PackageIdentity(),
            files
                .Where(static entry =>
                    entry.Role == InstallerPayloadFileRole.DependencyPackage)
                .Select((entry, index) => entry.Path ==
                    "dependencies/x64/microsoft.windowsappruntime.1.8.msix"
                    ? InstallerTestData.DependencyIdentity()
                    : InstallerTestData.DependencyIdentity(
                        entry.Path,
                        $"test.dependency.{index:D3}"))
                .ToArray(),
            machineFiles ?? InstallerTestData.MachineFiles(),
            files);

    private static InstallerPayloadFileEntry[] ReplaceFile(
        InstallerPayloadFileRole role,
        Func<InstallerPayloadFileEntry, InstallerPayloadFileEntry> replace) =>
        InstallerTestData.Manifest().Files
            .Select(entry => entry.Role == role ? replace(entry) : entry)
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

    private static InstallerPayloadFileEntry[] CreateBudgetFiles(
        long primaryLength,
        long dependencyLength,
        long certificateLength,
        long provenanceLength) =>
        InstallerTestData.Manifest().Files
            .Select(entry => entry.Role switch
            {
                InstallerPayloadFileRole.PrimaryPackage => entry with { Length = primaryLength },
                InstallerPayloadFileRole.DependencyPackage => entry with { Length = dependencyLength },
                InstallerPayloadFileRole.Certificate => entry with { Length = certificateLength },
                InstallerPayloadFileRole.Provenance => entry with { Length = provenanceLength },
                _ => entry,
            })
            .ToArray();

    private static InstallerPayloadFileEntry[] CreateFileCountManifest(int totalFileCount)
    {
        List<InstallerPayloadFileEntry> files =
        [
            InstallerTestData.Manifest().Files.Single(static entry =>
                entry.Role == InstallerPayloadFileRole.PrimaryPackage),
            InstallerTestData.Manifest().Files.Single(static entry =>
                entry.Role == InstallerPayloadFileRole.Certificate),
            InstallerTestData.Manifest().Files.Single(static entry =>
                entry.Role == InstallerPayloadFileRole.Provenance),
        ];
        for (int index = 0; index < totalFileCount - 3; index++)
        {
            files.Add(new InstallerPayloadFileEntry(
                $"dependencies/x64/dependency-{index:D3}.msix",
                InstallerPayloadFileRole.DependencyPackage,
                1,
                InstallerTestData.DependencyHash));
        }

        return files.OrderBy(static entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }
}
