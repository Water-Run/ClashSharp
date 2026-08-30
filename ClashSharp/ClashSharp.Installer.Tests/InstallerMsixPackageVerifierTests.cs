using System.IO.Compression;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMsixPackageVerifierTests
{
    private const string AppxManifestPath = "AppxManifest.xml";
    private const string AppxBlockMapPath = "AppxBlockMap.xml";
    private const string AppxSignaturePath = "AppxSignature.p7x";
    private const string PrimaryManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
          <Identity Name="67dc1dc3-13fd-46c5-84f4-2932d94b566f"
                    Publisher="CN=linzh"
                    Version="1.2.3.4"
                    ProcessorArchitecture="x64" />
          <Properties>
            <uap10:PackageIntegrity><uap10:Content Enforcement="on" /></uap10:PackageIntegrity>
          </Properties>
          <Dependencies>
            <PackageDependency Name="Microsoft.WindowsAppRuntime.1.8"
                               Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
                               MinVersion="8000.806.2252.0" />
          </Dependencies>
          <Applications>
            <Application Id="App"
                         Executable="ClashSharp.exe"
                         EntryPoint="Windows.FullTrustApplication" />
          </Applications>
        </Package>
        """;
    private const string DependencyManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="Microsoft.WindowsAppRuntime.1.8"
                    Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
                    Version="8000.900.1.0"
                    ProcessorArchitecture="x64" />
          <Properties><Framework>true</Framework></Properties>
        </Package>
        """;

    [Fact]
    public void VerifyPrimaryAcceptsCanonicalPackageAndLeavesCallerStreamOpen()
    {
        using MemoryStream package = CreatePackage(Encoding.UTF8.GetBytes(PrimaryManifest));

        InstallerMsixPackageVerifier.VerifyPrimary(
            package,
            InstallerTestData.Manifest(),
            CancellationToken.None);

        Assert.True(package.CanRead);
        package.Position = 0;
        Assert.Equal(0x50, package.ReadByte());
    }

    [Fact]
    public void VerifyDependencyAcceptsCanonicalFrameworkPackage()
    {
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(DependencyManifest),
            includeMachineFiles: false);

        InstallerMsixPackageVerifier.VerifyDependency(
            package,
            InstallerTestData.DependencyIdentity(),
            CancellationToken.None);

        Assert.True(package.CanRead);
    }

    [Theory]
    [InlineData(
        "Name=\"67dc1dc3-13fd-46c5-84f4-2932d94b566f\"",
        "Name=\"Contoso.Other\"")]
    [InlineData("Publisher=\"CN=linzh\"", "Publisher=\"CN=Someone Else\"")]
    [InlineData("Version=\"1.2.3.4\"", "Version=\"1.2.3.5\"")]
    [InlineData("ProcessorArchitecture=\"x64\"", "ProcessorArchitecture=\"arm64\"")]
    [InlineData("Id=\"App\"", "Id=\"Other\"")]
    [InlineData("Executable=\"ClashSharp.exe\"", "Executable=\"Other.exe\"")]
    [InlineData(
        "EntryPoint=\"Windows.FullTrustApplication\"",
        "EntryPoint=\"Windows.PartialTrustApplication\"")]
    public void VerifyPrimaryRejectsEveryBoundIdentityAndApplicationField(
        string expectedFragment,
        string replacement)
    {
        string manifest = ReplaceExactlyOnce(PrimaryManifest, expectedFragment, replacement);
        using MemoryStream package = CreatePackage(Encoding.UTF8.GetBytes(manifest));

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.package_identity_mismatch");
    }

    [Fact]
    public void VerifyPrimaryRejectsDisabledPackageIntegrityEnforcement()
    {
        string manifest = ReplaceExactlyOnce(
            PrimaryManifest,
            "Enforcement=\"on\"",
            "Enforcement=\"off\"");
        using MemoryStream package = CreatePackage(Encoding.UTF8.GetBytes(manifest));

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.package_integrity_contract_invalid");
    }

    [Theory]
    [InlineData(
        "Name=\"Microsoft.WindowsAppRuntime.1.8\"",
        "Name=\"Microsoft.WindowsAppRuntime.1.7\"")]
    [InlineData(
        "MinVersion=\"8000.806.2252.0\"",
        "MinVersion=\"8000.806.2253.0\"")]
    public void VerifyPrimaryRejectsDependencyRequirementMismatch(
        string expectedFragment,
        string replacement)
    {
        string manifest = ReplaceExactlyOnce(PrimaryManifest, expectedFragment, replacement);
        using MemoryStream package = CreatePackage(Encoding.UTF8.GetBytes(manifest));

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.package_dependency_contract_invalid");
    }

    [Theory]
    [InlineData(
        "Name=\"Microsoft.WindowsAppRuntime.1.8\"",
        "Name=\"Microsoft.WindowsAppRuntime.1.7\"")]
    [InlineData(
        "Publisher=\"CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US\"",
        "Publisher=\"CN=Contoso\"")]
    [InlineData("Version=\"8000.900.1.0\"", "Version=\"8000.900.2.0\"")]
    [InlineData("ProcessorArchitecture=\"x64\"", "ProcessorArchitecture=\"arm64\"")]
    public void VerifyDependencyRejectsEveryBoundIdentityField(
        string expectedFragment,
        string replacement)
    {
        string manifest = ReplaceExactlyOnce(DependencyManifest, expectedFragment, replacement);
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(manifest),
            includeMachineFiles: false);

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyDependency(
                package,
                InstallerTestData.DependencyIdentity(),
                CancellationToken.None),
            "installer.release.dependency_identity_mismatch");
    }

    [Fact]
    public void VerifyDependencyRejectsNonFrameworkPackage()
    {
        string manifest = ReplaceExactlyOnce(
            DependencyManifest,
            "<Framework>true</Framework>",
            "<Framework>false</Framework>");
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(manifest),
            includeMachineFiles: false);

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyDependency(
                package,
                InstallerTestData.DependencyIdentity(),
                CancellationToken.None),
            "installer.release.dependency_identity_mismatch");
    }

    [Theory]
    [InlineData(AppxManifestPath)]
    [InlineData(AppxBlockMapPath)]
    [InlineData(AppxSignaturePath)]
    public void VerifyPrimaryRejectsMissingRequiredArchiveEntry(string omittedPath)
    {
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(PrimaryManifest),
            omittedPath: omittedPath);

        AssertArchiveRejected(package);
    }

    [Theory]
    [InlineData(AppxManifestPath)]
    [InlineData(AppxBlockMapPath)]
    [InlineData(AppxSignaturePath)]
    public void VerifyPrimaryRejectsCaseChangedRequiredArchiveEntry(string renamedPath)
    {
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(PrimaryManifest),
            renamedPath: renamedPath);

        AssertArchiveRejected(package);
    }

    [Theory]
    [InlineData(AppxManifestPath)]
    [InlineData(AppxBlockMapPath)]
    [InlineData(AppxSignaturePath)]
    public void VerifyPrimaryRejectsDuplicateRequiredArchiveEntry(string duplicatedPath)
    {
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(PrimaryManifest),
            duplicatedPath: duplicatedPath);

        AssertArchiveRejected(package);
    }

    [Theory]
    [InlineData("Binaries/GeoData/ASN.mmdb")]
    [InlineData("Binaries/GeoData/Country.mmdb")]
    [InlineData("Binaries/GeoData/GeoIP.dat")]
    [InlineData("Binaries/GeoData/GeoSite.dat")]
    [InlineData("Binaries/GeoData/manifest.json")]
    [InlineData("Binaries/mihomo.exe")]
    [InlineData("Binaries/Service/ClashSharp.MihomoService.exe")]
    public void VerifyPrimaryRejectsEveryMissingMachineFile(string path)
    {
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(PrimaryManifest),
            omittedMachinePath: path);

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.machine_file_set_invalid");
    }

    [Theory]
    [InlineData("Binaries/GeoData/ASN.mmdb")]
    [InlineData("Binaries/GeoData/Country.mmdb")]
    [InlineData("Binaries/GeoData/GeoIP.dat")]
    [InlineData("Binaries/GeoData/GeoSite.dat")]
    [InlineData("Binaries/GeoData/manifest.json")]
    [InlineData("Binaries/mihomo.exe")]
    [InlineData("Binaries/Service/ClashSharp.MihomoService.exe")]
    public void VerifyPrimaryRejectsEveryChangedMachineFileHash(string path)
    {
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(PrimaryManifest),
            tamperedMachinePath: path);

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.machine_file_mismatch");
    }

    [Theory]
    [InlineData("Binaries/GeoData/extra.dat")]
    [InlineData("Binaries/Service/OtherService.exe")]
    public void VerifyPrimaryRejectsExtraMachineScopeFiles(string path)
    {
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(PrimaryManifest),
            extraMachinePath: path);

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.machine_file_set_invalid");
    }

    [Fact]
    public void VerifyPrimaryRejectsCaseCollidingMachineFile()
    {
        using MemoryStream package = CreatePackage(
            Encoding.UTF8.GetBytes(PrimaryManifest),
            duplicatedMachinePath: "Binaries/mihomo.exe");

        AssertArchiveRejected(package);
    }

    [Fact]
    public void VerifyPrimaryRejectsInvalidUtf8Manifest()
    {
        using MemoryStream package = CreatePackage([0xFF, 0xFE, 0xFA]);

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.package_metadata_invalid");
    }

    [Fact]
    public void VerifyPrimaryRejectsDocumentTypeDefinition()
    {
        string manifest = ReplaceExactlyOnce(
            PrimaryManifest,
            "<Package ",
            "<!DOCTYPE Package [<!ENTITY sample \"value\">]>\n<Package ");
        using MemoryStream package = CreatePackage(Encoding.UTF8.GetBytes(manifest));

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.package_metadata_invalid");
    }

    [Fact]
    public void VerifyPrimaryRejectsDuplicateRequiredManifestElement()
    {
        string manifest = ReplaceExactlyOnce(
            PrimaryManifest,
            "<Properties>",
            "<Properties /><Properties>");
        using MemoryStream package = CreatePackage(Encoding.UTF8.GetBytes(manifest));

        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.package_metadata_invalid");
    }

    [Fact]
    public void VerifyPrimaryRejectsNonSeekableStreamBeforeArchiveParsing()
    {
        using var package = new NonSeekableReadStream(
            CreatePackage(Encoding.UTF8.GetBytes(PrimaryManifest)));

        AssertArchiveRejected(package);
    }

    [Fact]
    public void VerifyPrimaryPreservesRequestedCancellation()
    {
        using MemoryStream package = CreatePackage(Encoding.UTF8.GetBytes(PrimaryManifest));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = Assert.Throws<OperationCanceledException>(() =>
            InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                cancellation.Token));
    }

    private static void AssertArchiveRejected(Stream package) =>
        AssertDiagnostic(
            () => InstallerMsixPackageVerifier.VerifyPrimary(
                package,
                InstallerTestData.Manifest(),
                CancellationToken.None),
            "installer.release.package_archive_invalid");

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }

    private static string ReplaceExactlyOnce(
        string source,
        string expectedFragment,
        string replacement)
    {
        int first = source.IndexOf(expectedFragment, StringComparison.Ordinal);
        if (first < 0
            || source.IndexOf(expectedFragment, first + expectedFragment.Length, StringComparison.Ordinal)
                >= 0)
        {
            throw new InvalidOperationException("The test mutation target must occur exactly once.");
        }

        return string.Concat(
            source.AsSpan(0, first),
            replacement,
            source.AsSpan(first + expectedFragment.Length));
    }

    private static MemoryStream CreatePackage(
        byte[] manifestBytes,
        string? omittedPath = null,
        string? renamedPath = null,
        string? duplicatedPath = null,
        bool includeMachineFiles = true,
        string? omittedMachinePath = null,
        string? tamperedMachinePath = null,
        string? duplicatedMachinePath = null,
        string? extraMachinePath = null)
    {
        (string Path, byte[] Bytes)[] canonicalEntries =
        [
            (AppxManifestPath, manifestBytes),
            (AppxBlockMapPath, "<BlockMap />"u8.ToArray()),
            (AppxSignaturePath, [0x01]),
        ];
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in canonicalEntries)
            {
                if (string.Equals(path, omittedPath, StringComparison.Ordinal))
                {
                    continue;
                }

                string archivePath = string.Equals(path, renamedPath, StringComparison.Ordinal)
                    ? path.ToLowerInvariant()
                    : path;
                WriteEntry(archive, archivePath, bytes);
            }

            if (duplicatedPath is not null)
            {
                byte[] duplicatedBytes = canonicalEntries.Single(entry =>
                    string.Equals(entry.Path, duplicatedPath, StringComparison.Ordinal)).Bytes;
                WriteEntry(archive, duplicatedPath, duplicatedBytes);
            }

            if (includeMachineFiles)
            {
                foreach (var machineFile in InstallerTestData.MachinePayload())
                {
                    string archivePath = machineFile.ArchivePath;
                    byte[] originalBytes = machineFile.Bytes;
                    if (string.Equals(
                            archivePath,
                            omittedMachinePath,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    byte[] bytes = originalBytes;
                    if (string.Equals(
                            archivePath,
                            tamperedMachinePath,
                            StringComparison.Ordinal))
                    {
                        bytes = originalBytes.ToArray();
                        bytes[0] ^= 0xFF;
                    }

                    WriteEntry(archive, archivePath, bytes);
                    if (string.Equals(
                            archivePath,
                            duplicatedMachinePath,
                            StringComparison.Ordinal))
                    {
                        WriteEntry(archive, archivePath.ToUpperInvariant(), originalBytes);
                    }
                }

                if (extraMachinePath is not null)
                {
                    WriteEntry(archive, extraMachinePath, "unexpected"u8);
                }
            }
        }

        output.Position = 0;
        return output;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        ReadOnlySpan<byte> bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        stream.Write(bytes);
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly Stream _inner;

        internal NonSeekableReadStream(Stream inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
