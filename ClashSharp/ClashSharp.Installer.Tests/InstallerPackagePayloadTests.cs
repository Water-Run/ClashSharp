using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// Payload trust: a corrupted or crafted MSIX can only be refused (or verified byte-for-byte), the
/// release manifest rejects every inconsistent shape, and package identities are derived, never trusted.
/// </summary>
public sealed class InstallerPackagePayloadTests
{
    private const int Seed = 20261109;

    private const string PrimaryManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
          <Identity Name="67dc1dc3-13fd-46c5-84f4-2932d94b566f" Publisher="CN=linzh" Version="1.2.3.4" ProcessorArchitecture="x64" />
          <Properties><uap10:PackageIntegrity><uap10:Content Enforcement="on" /></uap10:PackageIntegrity></Properties>
          <Dependencies>
            <PackageDependency Name="Microsoft.WindowsAppRuntime.1.8"
                               Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
                               MinVersion="8000.806.2252.0" />
          </Dependencies>
          <Applications><Application Id="App" Executable="ClashSharp.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
        </Package>
        """;

    private const string DependencyManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="Microsoft.WindowsAppRuntime.1.8"
                    Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
                    Version="8000.900.1.0" ProcessorArchitecture="x64" />
          <Properties><Framework>true</Framework></Properties>
        </Package>
        """;

    public static TheoryData<string, MutationFamily> PackageCases()
    {
        var data = new TheoryData<string, MutationFamily>();
        foreach (string kind in new[] { "primary-stored", "primary-deflated", "dependency-stored", "dependency-deflated" })
        {
            foreach (MutationFamily family in new[]
            {
                MutationFamily.Truncation, MutationFamily.BitFlip, MutationFamily.ByteDeletion,
                MutationFamily.ByteInsertion, MutationFamily.RandomEdits,
            })
            {
                data.Add(kind, family);
            }
        }

        return data;
    }

    public static TheoryData<string> ManifestMutationNames() => new(ManifestMutations().Keys.ToArray());

    [Theory]
    [MemberData(nameof(PackageCases))]
    public void CorruptedPackagesAreRefusedWithTheDeclaredExceptionAndNeverAcceptTamperedContent(
        string kind,
        MutationFamily family)
    {
        bool primary = kind.StartsWith("primary", StringComparison.Ordinal);
        byte[] package = primary
            ? BuildPackage(Encoding.UTF8.GetBytes(PrimaryManifest), kind.EndsWith("deflated", StringComparison.Ordinal))
            : BuildPackage(Encoding.UTF8.GetBytes(DependencyManifest), kind.EndsWith("deflated", StringComparison.Ordinal), machineFiles: false);
        Verify(primary, package);
        DocumentSpec spec = new(
            kind,
            () => [package],
            static bytes => bytes,
            static value => (byte[])value,
            StrictCanonical: false,
            package.Length);
        int accepted = 0;

        // Truncation is exhaustive; the other families are a seeded 1500-mutant sample to bound run time.
        IEnumerable<(string Label, byte[] Bytes)> mutants = family == MutationFamily.Truncation
            ? DocumentFuzz.Mutants(spec, package, family).Concat(ArchiveEnvelopes(package))
            : DocumentFuzz.Mutants(spec, package, family).Take(1500);
        foreach ((string label, byte[] mutant) in mutants)
        {
            try
            {
                Verify(primary, mutant);
            }
            catch (InstallerProtocolException exception)
            {
                InstallerProtocolValidation.ValidateDiagnosticCode(exception.DiagnosticCode);
                Assert.StartsWith("installer.release.", exception.DiagnosticCode, StringComparison.Ordinal);
                continue;
            }
            catch (Exception exception)
            {
                Assert.Fail($"{kind}/{family}/{label}: undeclared {exception.GetType().FullName}: {exception.Message}");
            }

            // Accepted: whatever was damaged must not have touched anything the verifier vouches for.
            accepted++;
            AssertVouchedContentIntact(primary, mutant, $"{kind}/{family}/{label}");
            Verify(primary, mutant);
        }

        Assert.True(accepted >= 0);
    }

    [Fact]
    public void ADecompressionBombOrLyingEntrySizeIsRefusedBeforeItIsInflated()
    {
        // A manifest entry whose real content is far larger than the budget.
        byte[] huge = Encoding.UTF8.GetBytes(PrimaryManifest + new string(' ', (int)InstallerPayloadBudgets.MaximumAppxManifestBytes + 1));
        byte[] oversized = BuildPackage(huge, deflate: true);
        long before = GC.GetAllocatedBytesForCurrentThread();

        InstallerProtocolException refusal = Assert.Throws<InstallerProtocolException>(() => Verify(primary: true, oversized));

        Assert.Equal("installer.release.package_archive_invalid", refusal.DiagnosticCode);
        Assert.True(
            GC.GetAllocatedBytesForCurrentThread() - before < 8 * 1024 * 1024,
            "The over-budget manifest must be refused without inflating it.");

        // Lie in the central directory (declared size smaller than the real data): must not be trusted.
        byte[] lying = BuildPackage(Encoding.UTF8.GetBytes(PrimaryManifest), deflate: false);
        PatchUncompressedSize(lying, "AppxManifest.xml", declared: 10);
        Assert.Throws<InstallerProtocolException>(() => Verify(primary: true, lying));

        // Lie the other way: a tiny declared machine file that actually contains more bytes.
        byte[] understated = BuildPackage(Encoding.UTF8.GetBytes(PrimaryManifest), deflate: false);
        PatchUncompressedSize(understated, "Binaries/mihomo.exe", declared: 3);
        Assert.Throws<InstallerProtocolException>(() => Verify(primary: true, understated));
    }

    [Fact]
    public void PathsThatEscapeCollideOrHideMachineFilesAreRefusedWhateverTheirSpelling()
    {
        string[] evil =
        [
            "../Binaries/mihomo.exe", "Binaries/../mihomo.exe", "/Binaries/mihomo.exe", "Binaries\\mihomo.exe",
            "Binaries//mihomo.exe", "Binaries/mihomo.exe/", "Binaries/mihomo.exe.", "Binaries/mihomo.exe ", "./Binaries/mihomo.exe",
            "Binaries/./mihomo.exe", "Binaries/Service/../mihomo.exe", new string('a', 600),
        ];
        foreach (string path in evil)
        {
            byte[] package = BuildPackage(Encoding.UTF8.GetBytes(PrimaryManifest), deflate: false, extraEntry: (path, "x"u8.ToArray()));

            Assert.Throws<InstallerProtocolException>(() => Verify(primary: true, package));
        }

        // Case-insensitive collisions are as dangerous as exact duplicates on Windows.
        byte[] collision = BuildPackage(Encoding.UTF8.GetBytes(PrimaryManifest), deflate: false, extraEntry: ("BINARIES/MIHOMO.EXE", "x"u8.ToArray()));
        Assert.Throws<InstallerProtocolException>(() => Verify(primary: true, collision));
    }

    [Fact]
    public void EmptyAndOddlyEncodedManifestsAreRefusedWithTheDeclaredException()
    {
        byte[][] manifests =
        [
            [0xEF, 0xBB, 0xBF],
            Encoding.UTF8.GetBytes("<Package/>"),
            Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Package xmlns=\"http://example.invalid/\"/>"),
            Encoding.UTF8.GetBytes("<!DOCTYPE Package [<!ENTITY x \"y\">]><Package/>"),
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(PrimaryManifest), 0xFF],
            Encoding.Unicode.GetBytes(PrimaryManifest),
            Encoding.UTF8.GetBytes(PrimaryManifest.Replace("</Package>", "<Identity/></Package>", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(new string('<', 4096)),
        ];
        foreach (byte[] manifest in manifests)
        {
            byte[] package = BuildPackage(manifest, deflate: false);
            Exception? failure = Record.Exception(() => Verify(primary: true, package));

            InstallerProtocolException refusal = Assert.IsType<InstallerProtocolException>(failure);
            Assert.StartsWith("installer.release.", refusal.DiagnosticCode, StringComparison.Ordinal);
        }

        // A BOM-prefixed but otherwise canonical manifest is the one tolerated spelling.
        byte[] bom = BuildPackage([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(PrimaryManifest)], deflate: false);
        Verify(primary: true, bom);
    }

    [Theory]
    [MemberData(nameof(ManifestMutationNames))]
    public void EveryInconsistentManifestShapeIsRefusedByValidationAndByTheCodec(string mutation)
    {
        InstallerReleaseManifest manifest = ManifestMutations()[mutation]();

        InstallerProtocolException refusal = Assert.Throws<InstallerProtocolException>(manifest.Validate);

        // The version grammar is shared with requests, so its refusal keeps the shared stable code.
        Assert.True(
            refusal.DiagnosticCode.StartsWith("installer.release.", StringComparison.Ordinal)
                || refusal.DiagnosticCode == "installer.request.package_version_invalid",
            refusal.DiagnosticCode);
        Assert.Throws<InstallerProtocolException>(() => InstallerReleaseManifestCodec.Serialize(manifest));
        Assert.Throws<InstallerProtocolException>(() => manifest.CreateVerifiedRelease(true, true));
    }

    [Fact]
    public void AValidManifestSurvivesEveryOrderingOfItsInputsAndDerivesTheExactRelease()
    {
        InstallerReleaseManifest manifest = InstallerTestData.Manifest();
        manifest.Validate();
        VerifiedInstallerRelease release = manifest.CreateVerifiedRelease(packagePayloadAvailable: true, certificatePayloadAvailable: false);

        Assert.True(manifest.Matches(release));
        Assert.False(release.CertificatePayloadAvailable);
        foreach (VerifiedInstallerRelease other in new[]
        {
            release with { ExpectedPackageVersion = "9.9.9.9" },
            release with { InstallerPayloadSha256 = InstallerTestData.OtherHash },
            release with { PackageCertificateThumbprint = InstallerTestData.AuthenticodeThumbprint },
            release with { CertificateSha256 = InstallerTestData.OtherHash },
        })
        {
            Assert.False(manifest.Matches(other));
        }

        // Two dependencies in canonical order are accepted; the same two reversed are not.
        InstallerDependencyPackageIdentity second = InstallerTestData.DependencyIdentity(
            "dependencies/x64/microsoft.windowsappruntime.1.9.msix", "Microsoft.WindowsAppRuntime.1.9");
        InstallerPayloadFileEntry[] files = [.. manifest.Files, new(second.Path, InstallerPayloadFileRole.DependencyPackage, 64, InstallerTestData.CertificateHash)];
        InstallerPayloadFileEntry[] ordered = files.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray();
        InstallerReleaseManifest two = Rebuild(manifest, p => p with { Dependencies = [manifest.Dependencies[0], second], Files = ordered });
        two.Validate();
        Assert.Equal(two.Dependencies.Count, InstallerReleaseManifestCodec.Parse(InstallerReleaseManifestCodec.Serialize(two)).Dependencies.Count);
        Assert.Throws<InstallerProtocolException>(() => Rebuild(manifest, p => p with { Dependencies = [second, manifest.Dependencies[0]], Files = ordered }).Validate());
        Assert.Throws<InstallerProtocolException>(() => Rebuild(manifest, p => p with { Files = [.. ordered.Reverse()] }).Validate());
        Assert.Throws<ArgumentNullException>(() => manifest.Matches(null!));
    }

    [Fact]
    public void PublisherIdsAreDerivedFromThePublisherNotTakenOnTrust()
    {
        // Known (publisher, id) pairs from real packages: the id is a pure function of the publisher text.
        Assert.Equal(InstallerTestData.PackagePublisherId, DerivedId(InstallerTestData.PackagePublisher));
        Assert.Equal(InstallerTestData.DependencyPublisherId, DerivedId(InstallerTestData.DependencyPublisher));
        InstallerPackageIdentity identity = InstallerTestData.PackageIdentity();
        identity.Validate(InstallerTestData.Version);

        InstallerPackageIdentity[] invalid =
        [
            identity with { Publisher = "CN=someone-else" },
            identity with { PublisherId = "8wekyb3d8bbwe" },
            identity with { PublisherId = identity.PublisherId.ToUpperInvariant() },
            identity with { PublisherId = identity.PublisherId[..12] },
            identity with { Name = "ab" },
            identity with { Name = new string('a', 51) },
            identity with { Name = "bad name" },
            identity with { Architecture = "arm64" },
            identity with { Architecture = "X64" },
            identity with { ResourceId = "en-us" },
            identity with { PackageFullName = identity.PackageFullName + "x" },
            identity with { PackageFamilyName = "other_" + identity.PublisherId },
            identity with { ApplicationId = "" },
            identity with { ApplicationId = "-App" },
            identity with { ApplicationExecutable = "ClashSharp" },
            identity with { ApplicationExecutable = "dir\\ClashSharp.exe" },
            identity with { ApplicationExecutable = "dir/ClashSharp.exe" },
            identity with { ApplicationExecutable = "C:ClashSharp.exe" },
            identity with { ApplicationEntryPoint = "Windows..FullTrustApplication" },
            identity with { ApplicationEntryPoint = " " },
            identity with { Publisher = " CN=linzh" },
            identity with { Publisher = "CN=linzh\0" },
            identity with { Publisher = "CN" },
        ];
        foreach (InstallerPackageIdentity candidate in invalid)
        {
            InstallerProtocolException refusal = Assert.Throws<InstallerProtocolException>(() => candidate.Validate(InstallerTestData.Version));
            Assert.Equal("installer.release.package_identity_invalid", refusal.DiagnosticCode);
        }

        Assert.Throws<InstallerProtocolException>(() => identity.Validate("1.2.3"));
        Assert.Throws<InstallerProtocolException>(() => identity.Validate("1.2.3.5"));

        InstallerDependencyPackageIdentity dependency = InstallerTestData.DependencyIdentity();
        dependency.Validate();
        InstallerDependencyPackageIdentity[] badDependencies =
        [
            dependency with { Path = "Dependencies/x64/a.msix" },
            dependency with { Path = "dependencies/x86/a.msix" },
            dependency with { Path = "dependencies/x64/nested/a.msix" },
            dependency with { Path = "dependencies/x64/.msix" },
            dependency with { MinimumVersion = "9000.0.0.0" },
            dependency with { MinimumVersion = "1.0" },
            dependency with { Version = "08000.900.1.0" },
            dependency with { ResourceId = "x" },
            dependency with { Architecture = "arm64" },
            dependency with { Publisher = "CN=Evil" },
        ];
        foreach (InstallerDependencyPackageIdentity candidate in badDependencies)
        {
            Assert.Equal(
                "installer.release.dependency_identity_invalid",
                Assert.Throws<InstallerProtocolException>(candidate.Validate).DiagnosticCode);
        }
    }

    [Fact]
    public void InstalledPackageIdentityIsValidatedAndMatchedAgainstTheReleaseFamilyExactly()
    {
        InstallerReleaseManifest manifest = InstallerTestData.Manifest();
        InstallerPackageIdentity expected = manifest.PackageIdentity;
        InstallerInstalledPackage installed = new(
            expected.Name, expected.Publisher, expected.PublisherId, "1.2.3.3", expected.Architecture, expected.ResourceId,
            $"{expected.Name}_1.2.3.3_{expected.Architecture}_{expected.ResourceId}_{expected.PublisherId}",
            expected.PackageFamilyName, IsHealthy: true);

        installed.Validate();
        Assert.True(installed.MatchesReleaseFamily(manifest));
        Assert.True((installed with { IsHealthy = false }).MatchesReleaseFamily(manifest));

        InstallerInstalledPackage[] foreign =
        [
            installed with { Name = "Contoso.Other", PackageFamilyName = "Contoso.Other_" + expected.PublisherId, PackageFullName = $"Contoso.Other_1.2.3.3_x64__{expected.PublisherId}" },
            installed with { Publisher = "CN=Other", PublisherId = DerivedId("CN=Other"), PackageFamilyName = $"{expected.Name}_{DerivedId("CN=Other")}", PackageFullName = $"{expected.Name}_1.2.3.3_x64__{DerivedId("CN=Other")}" },
        ];
        foreach (InstallerInstalledPackage package in foreign)
        {
            package.Validate();
            Assert.False(package.MatchesReleaseFamily(manifest));
        }

        InstallerInstalledPackage[] invalid =
        [
            installed with { Version = "1.2.3" },
            installed with { PackageFullName = installed.PackageFullName + "x" },
            installed with { PackageFamilyName = "x_" + expected.PublisherId },
            installed with { Architecture = "arm64" },
            installed with { PublisherId = "8wekyb3d8bbwe" },
            installed with { ResourceId = "ja-jp" },
        ];
        foreach (InstallerInstalledPackage package in invalid)
        {
            Assert.Equal(
                "installer.package.installed_identity_invalid",
                Assert.Throws<InstallerProtocolException>(package.Validate).DiagnosticCode);
            Assert.Throws<InstallerProtocolException>(() => package.MatchesReleaseFamily(manifest));
        }

        Assert.Throws<ArgumentNullException>(() => installed.MatchesReleaseFamily(null!));
    }

    [Fact]
    public void FileEntriesAcceptOnlyTheirRoleShapeAndCanonicalLowercasePaths()
    {
        string hash = InstallerTestData.Hash;
        InstallerPayloadFileEntry[] valid =
        [
            new("clashsharp_1.2.3.4_x64.msix", InstallerPayloadFileRole.PrimaryPackage, 1, hash),
            new("clashsharp_temporarykey.cer", InstallerPayloadFileRole.Certificate, InstallerPayloadBudgets.MaximumCertificateBytes, hash),
            new("payload-provenance.json", InstallerPayloadFileRole.Provenance, InstallerPayloadBudgets.MaximumProvenanceBytes, hash),
            new("dependencies/x64/a.msix", InstallerPayloadFileRole.DependencyPackage, InstallerPayloadBudgets.MaximumFileBytes, hash),
        ];
        Assert.All(valid, static entry => entry.Validate());

        InstallerPayloadFileEntry[] invalid =
        [
            valid[0] with { Path = "sub/a.msix" },
            valid[0] with { Path = "a.exe" },
            valid[0] with { Path = "A.msix" },
            valid[0] with { Length = 0 },
            valid[0] with { Length = -1 },
            valid[0] with { Length = InstallerPayloadBudgets.MaximumFileBytes + 1 },
            valid[0] with { Sha256 = hash.ToUpperInvariant() },
            valid[0] with { Role = (InstallerPayloadFileRole)99 },
            valid[1] with { Path = "other.cer" },
            valid[1] with { Length = InstallerPayloadBudgets.MaximumCertificateBytes + 1 },
            valid[2] with { Path = "payload-provenance.txt" },
            valid[2] with { Length = InstallerPayloadBudgets.MaximumProvenanceBytes + 1 },
            valid[3] with { Path = "dependencies/x64/" },
            valid[3] with { Path = "dependencies/x64/.msix" },
            valid[3] with { Path = "dependencies/arm64/a.msix" },
            valid[3] with { Path = "dependencies/x64/n/a.msix" },
            valid[0] with { Path = "con.msix" },
            valid[0] with { Path = "nul" + ".msix" },
            valid[0] with { Path = "a b.msix" },
            valid[0] with { Path = "a\u00e9.msix" },
            valid[0] with { Path = "a/../b.msix" },
            valid[0] with { Path = "a//b.msix" },
            valid[0] with { Path = "/a.msix" },
            valid[0] with { Path = "a.msix/" },
            valid[0] with { Path = "x/x/x/a.msix" },
            valid[0] with { Path = new string('a', 236) + ".msix" },
            valid[0] with { Path = "" },
            valid[0] with { Path = "lpt1.msix" },
            valid[0] with { Path = "clock$.msix" },
        ];
        foreach (InstallerPayloadFileEntry entry in invalid)
        {
            Assert.True(
                Throws(entry.Validate),
                $"Accepted {entry.Path} / {entry.Role} / {entry.Length}");
        }
    }

    // ---- package builders ------------------------------------------------------------------------------

    private static IEnumerable<(string Label, byte[] Bytes)> ArchiveEnvelopes(byte[] package)
    {
        yield return ("prefix-1", [0x00, .. package]);
        yield return ("prefix-4096", [.. new byte[4096], .. package]);
        yield return ("suffix-1", [.. package, 0x00]);
        yield return ("suffix-4096", [.. package, .. new byte[4096]]);
        yield return ("suffix-70000", [.. package, .. new byte[70000]]);
        yield return ("doubled", [.. package, .. package]);
        yield return ("reversed", [.. package.Reverse()]);
        yield return ("zeroed", new byte[package.Length]);
        yield return ("ones", Enumerable.Repeat((byte)0xFF, package.Length).ToArray());
    }

    private static byte[] BuildPackage(
        byte[] manifestBytes,
        bool deflate,
        bool machineFiles = true,
        (string Path, byte[] Bytes)? extraEntry = null)
    {
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            CompressionLevel level = deflate ? CompressionLevel.SmallestSize : CompressionLevel.NoCompression;
            Write(archive, "AppxManifest.xml", manifestBytes, level);
            Write(archive, "AppxBlockMap.xml", "<BlockMap />"u8.ToArray(), level);
            Write(archive, "AppxSignature.p7x", [0x01, 0x02, 0x03], level);
            if (machineFiles)
            {
                foreach ((string archivePath, _, byte[] bytes) in InstallerTestData.MachinePayload())
                {
                    Write(archive, archivePath, bytes, level);
                }
            }

            if (extraEntry is { } extra)
            {
                Write(archive, extra.Path, extra.Bytes, level);
            }
        }

        return output.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] bytes, CompressionLevel level)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, level);
        using Stream stream = entry.Open();
        stream.Write(bytes);
    }

    private static void Verify(bool primary, byte[] package)
    {
        using MemoryStream stream = new(package);
        if (primary)
        {
            InstallerMsixPackageVerifier.VerifyPrimary(stream, InstallerTestData.Manifest(), CancellationToken.None);
        }
        else
        {
            InstallerMsixPackageVerifier.VerifyDependency(stream, InstallerTestData.DependencyIdentity(), CancellationToken.None);
        }
    }

    /// <summary>For an accepted package, the machine payload and manifest identity must be exactly the trusted ones.</summary>
    private static void AssertVouchedContentIntact(bool primary, byte[] package, string label)
    {
        using MemoryStream stream = new(package);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        if (primary)
        {
            foreach ((string archivePath, _, byte[] expected) in InstallerTestData.MachinePayload())
            {
                ZipArchiveEntry entry = Assert.Single(
                    archive.Entries,
                    candidate => string.Equals(candidate.FullName, archivePath, StringComparison.OrdinalIgnoreCase));
                using MemoryStream copy = new();
                using Stream content = entry.Open();
                content.CopyTo(copy);
                Assert.True(expected.AsSpan().SequenceEqual(copy.ToArray()), $"{label}: accepted package carries different {archivePath}.");
            }
        }

        ZipArchiveEntry manifest = Assert.Single(archive.Entries, static candidate => candidate.FullName == "AppxManifest.xml");
        using StreamReader reader = new(manifest.Open(), new UTF8Encoding(false, true));
        string xml = reader.ReadToEnd();
        string identity = primary ? "Name=\"67dc1dc3-13fd-46c5-84f4-2932d94b566f\"" : "Name=\"Microsoft.WindowsAppRuntime.1.8\"";
        Assert.Contains(identity, xml, StringComparison.Ordinal);
    }

    private static void PatchUncompressedSize(byte[] package, string entryPath, int declared)
    {
        // Rewrite the declared uncompressed size in the central directory header of one entry.
        byte[] name = Encoding.ASCII.GetBytes(entryPath);
        for (int index = 0; index + 46 + name.Length <= package.Length; index++)
        {
            if (package[index] == 0x50 && package[index + 1] == 0x4B && package[index + 2] == 0x01 && package[index + 3] == 0x02
                && package.AsSpan(index + 46, name.Length).SequenceEqual(name))
            {
                BitConverter.TryWriteBytes(package.AsSpan(index + 24, 4), declared);
                return;
            }
        }

        throw new InvalidOperationException("Central directory entry not found.");
    }

    // ---- manifest mutation matrix ------------------------------------------------------------------------

    private static Dictionary<string, Func<InstallerReleaseManifest>> ManifestMutations()
    {
        InstallerReleaseManifest baseline = InstallerTestData.Manifest();
        InstallerPackageIdentity identity = baseline.PackageIdentity;
        InstallerPayloadFileEntry primaryFile = baseline.Files.Single(static file => file.Role == InstallerPayloadFileRole.PrimaryPackage);
        InstallerPayloadFileEntry certificateFile = baseline.Files.Single(static file => file.Role == InstallerPayloadFileRole.Certificate);
        InstallerPayloadFileEntry provenanceFile = baseline.Files.Single(static file => file.Role == InstallerPayloadFileRole.Provenance);
        InstallerPayloadFileEntry dependencyFile = baseline.Files.Single(static file => file.Role == InstallerPayloadFileRole.DependencyPackage);
        InstallerMachinePayloadFileEntry machine = baseline.MachineFiles[0];

        return new Dictionary<string, Func<InstallerReleaseManifest>>
        {
            ["schema-0"] = () => Rebuild(baseline, p => p with { Schema = 0 }),
            ["schema-next"] = () => Rebuild(baseline, p => p with { Schema = InstallerReleaseManifest.CurrentSchema + 1 }),
            ["version-short"] = () => Rebuild(baseline, p => p with { Version = "1.2.3" }),
            ["version-leading-zero"] = () => Rebuild(baseline, p => p with { Version = "01.2.3.4" }),
            ["payload-hash-upper"] = () => Rebuild(baseline, p => p with { Payload = InstallerTestData.Hash.ToUpperInvariant() }),
            ["payload-hash-short"] = () => Rebuild(baseline, p => p with { Payload = InstallerTestData.Hash[..63] }),
            ["authenticode-lower"] = () => Rebuild(baseline, p => p with { Authenticode = InstallerTestData.AuthenticodeThumbprint.ToLowerInvariant() }),
            ["authenticode-short"] = () => Rebuild(baseline, p => p with { Authenticode = InstallerTestData.AuthenticodeThumbprint[..39] }),
            ["certificate-thumbprint-lower"] = () => Rebuild(baseline, p => p with { Thumbprint = InstallerTestData.CertificateThumbprint.ToLowerInvariant() }),
            ["certificate-hash-upper"] = () => Rebuild(baseline, p => p with { CertificateHash = InstallerTestData.CertificateHash.ToUpperInvariant() }),
            ["identity-null"] = () => Rebuild(baseline, p => p with { Identity = null! }),
            ["identity-name"] = () => Rebuild(baseline, p => p with { Identity = identity with { Name = "ab" } }),
            ["identity-arch"] = () => Rebuild(baseline, p => p with { Identity = identity with { Architecture = "arm64" } }),
            ["identity-publisher-id"] = () => Rebuild(baseline, p => p with { Identity = identity with { PublisherId = "8wekyb3d8bbwe" } }),
            ["identity-version-binding"] = () => Rebuild(baseline, p => p with { Version = "1.2.3.5" }),
            ["dependencies-null"] = () => Rebuild(baseline, p => p with { Dependencies = null! }),
            ["dependencies-empty"] = () => Rebuild(baseline, p => p with { Dependencies = [] }),
            ["dependencies-null-element"] = () => Rebuild(baseline, p => p with { Dependencies = [null!] }),
            ["dependencies-duplicate"] = () => Rebuild(baseline, p => p with { Dependencies = [baseline.Dependencies[0], baseline.Dependencies[0]] }),
            ["dependencies-path-mismatch"] = () => Rebuild(baseline, p => p with { Dependencies = [baseline.Dependencies[0] with { Path = "dependencies/x64/other.msix" }] }),
            ["dependencies-identity-invalid"] = () => Rebuild(baseline, p => p with { Dependencies = [baseline.Dependencies[0] with { Publisher = "CN=Evil" }] }),
            ["machine-null"] = () => Rebuild(baseline, p => p with { MachineFiles = null! }),
            ["machine-missing-one"] = () => Rebuild(baseline, p => p with { MachineFiles = [.. baseline.MachineFiles.Skip(1)] }),
            ["machine-extra"] = () => Rebuild(baseline, p => p with { MachineFiles = [.. baseline.MachineFiles, new("binaries/zzz.dll", 1, InstallerTestData.Hash)] }),
            ["machine-null-element"] = () => Rebuild(baseline, p => p with { MachineFiles = [.. baseline.MachineFiles.Skip(1), null!] }),
            ["machine-duplicate"] = () => Rebuild(baseline, p => p with { MachineFiles = [baseline.MachineFiles[0], .. baseline.MachineFiles.Skip(1).Take(5), baseline.MachineFiles[0]] }),
            ["machine-unordered"] = () => Rebuild(baseline, p => p with { MachineFiles = [.. baseline.MachineFiles.Reverse()] }),
            ["machine-wrong-path"] = () => Rebuild(baseline, p => p with { MachineFiles = [machine with { Path = "binaries/geodata/other.dat" }, .. baseline.MachineFiles.Skip(1)] }),
            ["machine-zero-length"] = () => Rebuild(baseline, p => p with { MachineFiles = [machine with { Length = 0 }, .. baseline.MachineFiles.Skip(1)] }),
            ["machine-hash"] = () => Rebuild(baseline, p => p with { MachineFiles = [machine with { Sha256 = "x" }, .. baseline.MachineFiles.Skip(1)] }),
            ["machine-oversize-asset"] = () => Rebuild(baseline, p => p with { MachineFiles = [machine with { Length = InstallerPayloadBudgets.MaximumGeoDataAssetBytes + 1 }, .. baseline.MachineFiles.Skip(1)] }),
            ["machine-total-budget"] = () => Rebuild(baseline, p => p with
            {
                MachineFiles = [.. baseline.MachineFiles.Select(static file => file.Path switch
            {
                "binaries/mihomo.exe" or "binaries/service/clashsharp.mihomoservice.exe" => file with { Length = InstallerPayloadBudgets.MaximumFileBytes },
                "binaries/geodata/asn.mmdb" => file with { Length = InstallerPayloadBudgets.MaximumGeoDataAssetBytes },
                _ => file,
            })]
            }),
            ["files-null"] = () => Rebuild(baseline, p => p with { Files = null! }),
            ["files-too-few"] = () => Rebuild(baseline, p => p with { Files = [primaryFile, certificateFile, provenanceFile] }),
            ["files-too-many"] = () => Rebuild(baseline, p => p with { Files = [.. Enumerable.Range(0, 70).Select(static i => new InstallerPayloadFileEntry($"dependencies/x64/d{i:D3}.msix", InstallerPayloadFileRole.DependencyPackage, 1, InstallerTestData.Hash)), primaryFile, certificateFile, provenanceFile] }),
            ["files-null-element"] = () => Rebuild(baseline, p => p with { Files = [.. baseline.Files, null!] }),
            ["files-duplicate"] = () => Rebuild(baseline, p => p with { Files = [.. baseline.Files, baseline.Files[0]] }),
            ["files-unordered"] = () => Rebuild(baseline, p => p with { Files = [.. baseline.Files.Reverse()] }),
            ["files-two-primaries"] = () => Rebuild(baseline, p => p with { Files = OrderedFiles(baseline.Files.Append(primaryFile with { Path = "zzz.msix" })) }),
            ["files-no-certificate"] = () => Rebuild(baseline, p => p with { Files = [.. baseline.Files.Where(static file => file.Role != InstallerPayloadFileRole.Certificate)] }),
            ["files-no-provenance"] = () => Rebuild(baseline, p => p with { Files = [.. baseline.Files.Where(static file => file.Role != InstallerPayloadFileRole.Provenance), primaryFile with { Path = "zz.msix" }] }),
            ["files-primary-hash"] = () => Rebuild(baseline, p => p with { Files = OrderedFiles(baseline.Files.Select(file => file.Role == InstallerPayloadFileRole.PrimaryPackage ? file with { Sha256 = InstallerTestData.OtherHash } : file)) }),
            ["files-certificate-hash"] = () => Rebuild(baseline, p => p with { Files = OrderedFiles(baseline.Files.Select(file => file.Role == InstallerPayloadFileRole.Certificate ? file with { Sha256 = InstallerTestData.OtherHash } : file)) }),
            ["files-dependency-count"] = () => Rebuild(baseline, p => p with { Files = OrderedFiles(baseline.Files.Append(dependencyFile with { Path = "dependencies/x64/zzz.msix" })) }),
            ["files-extra-directory"] = () => Rebuild(baseline, p => p with { Files = OrderedFiles(baseline.Files.Append(new InstallerPayloadFileEntry("dependencies/x64/z/q.msix", InstallerPayloadFileRole.DependencyPackage, 1, InstallerTestData.Hash))) }),
            ["files-total-budget"] = () => Rebuild(baseline, p => p with { Files = OrderedFiles(baseline.Files.Select(file => file with { Length = file.Role == InstallerPayloadFileRole.PrimaryPackage || file.Role == InstallerPayloadFileRole.DependencyPackage ? InstallerPayloadBudgets.MaximumFileBytes : file.Length }).Append(new InstallerPayloadFileEntry("dependencies/x64/zzz.msix", InstallerPayloadFileRole.DependencyPackage, InstallerPayloadBudgets.MaximumFileBytes, InstallerTestData.Hash))) }),
            ["files-bad-path"] = () => Rebuild(baseline, p => p with { Files = OrderedFiles(baseline.Files.Select(file => file.Role == InstallerPayloadFileRole.PrimaryPackage ? file with { Path = "Primary.msix" } : file)) }),
            ["files-zero-length"] = () => Rebuild(baseline, p => p with { Files = OrderedFiles(baseline.Files.Select(file => file.Role == InstallerPayloadFileRole.Provenance ? file with { Length = 0 } : file)) }),
        };

        InstallerPayloadFileEntry[] OrderedFiles(IEnumerable<InstallerPayloadFileEntry> files) =>
            [.. files.OrderBy(static file => file.Path, StringComparer.Ordinal)];
    }

    private sealed record Parts(
        int Schema,
        string Version,
        string Payload,
        string Authenticode,
        string Thumbprint,
        string CertificateHash,
        InstallerPackageIdentity Identity,
        IReadOnlyList<InstallerDependencyPackageIdentity> Dependencies,
        IReadOnlyList<InstallerMachinePayloadFileEntry> MachineFiles,
        IReadOnlyList<InstallerPayloadFileEntry> Files);

    private static InstallerReleaseManifest Rebuild(InstallerReleaseManifest source, Func<Parts, Parts> change)
    {
        Parts parts = change(new Parts(
            source.Schema,
            source.ExpectedPackageVersion,
            source.InstallerPayloadSha256,
            source.AuthenticodeCertificateThumbprint,
            source.PackageCertificateThumbprint,
            source.CertificateSha256,
            source.PackageIdentity,
            source.Dependencies,
            source.MachineFiles,
            source.Files));
        return new InstallerReleaseManifest(
            parts.Schema,
            parts.Version,
            parts.Payload,
            parts.Authenticode,
            parts.Thumbprint,
            parts.CertificateHash,
            parts.Identity,
            parts.Dependencies,
            parts.MachineFiles,
            parts.Files);
    }

    private static string DerivedId(string publisher)
    {
        // Independent restatement of the Windows publisher-id algorithm: the first 64 bits of the
        // SHA-256 of the UTF-16LE publisher, padded with one zero bit, as thirteen base-32 digits.
        byte[] digest = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        ulong head = BitConverter.ToUInt64(digest.AsSpan(0, 8).ToArray().Reverse().ToArray());
        UInt128 value = (UInt128)head << 1;
        StringBuilder id = new();
        for (int index = 0; index < 13; index++)
        {
            id.Append(alphabet[(int)((value >> (60 - (5 * index))) & 0x1F)]);
        }

        return id.ToString();
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InstallerProtocolException)
        {
            return true;
        }
    }
}
