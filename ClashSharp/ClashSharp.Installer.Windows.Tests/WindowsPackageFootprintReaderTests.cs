using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsPackageFootprintReaderTests
{
    private static readonly byte[] Signature = "opaque-test-signature"u8.ToArray();
    private static readonly byte[] BlockMap = "opaque-test-blockmap"u8.ToArray();

    [Fact]
    public async Task CandidateAndInstalledBytesMatchAndAllObservationHandlesDrain()
    {
        using var fixture = Fixture();
        await using WindowsInstallerReleaseLease lease = CreateCandidate(fixture,
            [("AppxSignature.p7x", Signature), ("AppxBlockMap.xml", BlockMap)], out InstallerPayloadFileEntry entry);
        string root = CreateInstalled(fixture);
        var reader = new WindowsPackageFootprintReader();

        WindowsPackageFootprint candidate = reader.ReadCandidate(lease, entry, CancellationToken.None);
        WindowsPackageFootprint installed = reader.ReadInstalled(root, CancellationToken.None);

        Assert.Equal(candidate, installed);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Signature)), installed.SignatureSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(BlockMap)), installed.BlockMapSha256);
        Assert.Equal(Signature, File.ReadAllBytes(Path.Combine(root, "AppxSignature.p7x")));
        foreach (string path in Directory.EnumerateFiles(root))
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Directory.Move(root, Path.Combine(fixture.RootDirectory, "moved-installed"));
        await lease.DisposeAsync();
        using var candidateExclusive = new FileStream(Path.Combine(fixture.RootDirectory, entry.Path), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-case")]
    public async Task MissingOrAmbiguousArchiveFootprintsAreRejected(string scenario)
    {
        using var fixture = Fixture();
        var entries = new List<(string, byte[])> { ("AppxBlockMap.xml", BlockMap) };
        if (scenario == "duplicate")
        {
            entries.Add(("AppxSignature.p7x", Signature));
            entries.Add(("appxsignature.p7x", Signature));
        }
        else if (scenario == "wrong-case")
        {
            entries.Add(("appxsignature.p7x", Signature));
        }
        await using WindowsInstallerReleaseLease lease = CreateCandidate(fixture, entries, out InstallerPayloadFileEntry entry);

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsPackageFootprintReader().ReadCandidate(lease, entry, CancellationToken.None));

        Assert.Equal("installer.package.footprint_invalid", failure.DiagnosticCode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void EmptyOrOversizedInstalledFootprintsAreBoundedAndReleaseHandles(bool signature, bool oversized)
    {
        using var fixture = Fixture();
        string root = CreateInstalled(fixture);
        string path = Path.Combine(root, signature ? "AppxSignature.p7x" : "AppxBlockMap.xml");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            int maximum = signature ? WindowsPackageFootprintReader.MaximumSignatureBytes : WindowsPackageFootprintReader.MaximumBlockMapBytes;
            stream.SetLength(oversized ? (long)maximum + 1 : 0);
        }

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsPackageFootprintReader().ReadInstalled(root, CancellationToken.None));

        Assert.Equal("installer.package.footprint_invalid", failure.DiagnosticCode);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void MissingInstalledBlockMapDoesNotLeakTheOpenedSignatureHandle()
    {
        using var fixture = Fixture();
        string root = CreateInstalled(fixture);
        File.Delete(Path.Combine(root, "AppxBlockMap.xml"));

        Assert.Throws<Win32Exception>(() => new WindowsPackageFootprintReader().ReadInstalled(root, CancellationToken.None));

        using var exclusive = new FileStream(Path.Combine(root, "AppxSignature.p7x"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void RelativeInstalledPathIsRejectedBeforeFileInspection()
    {
        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsPackageFootprintReader().ReadInstalled("relative-root", CancellationToken.None));

        Assert.Equal("installer.package.installed_path_invalid", failure.DiagnosticCode);
    }

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);

    private static string CreateInstalled(WindowsPayloadFixture fixture)
    {
        string root = Path.Combine(fixture.RootDirectory, "installed");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "AppxSignature.p7x"), Signature);
        File.WriteAllBytes(Path.Combine(root, "AppxBlockMap.xml"), BlockMap);
        return root;
    }

    private static WindowsInstallerReleaseLease CreateCandidate(WindowsPayloadFixture fixture,
        IEnumerable<(string Name, byte[] Bytes)> entries, out InstallerPayloadFileEntry entry)
    {
        InstallerReleaseManifest original = fixture.Manifest;
        InstallerPayloadFileEntry primary = original.Files.Single(file => file.Role == InstallerPayloadFileRole.PrimaryPackage);
        string path = Path.Combine(fixture.RootDirectory, primary.Path);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach ((string name, byte[] bytes) in entries)
            {
                using Stream output = archive.CreateEntry(name).Open();
                output.Write(bytes);
            }
        }
        byte[] package = File.ReadAllBytes(path);
        entry = primary with { Length = package.LongLength, Sha256 = Convert.ToHexStringLower(SHA256.HashData(package)) };
        InstallerPayloadFileEntry primaryEntry = entry;
        var manifest = new InstallerReleaseManifest(original.Schema, original.ExpectedPackageVersion, entry.Sha256,
            original.AuthenticodeCertificateThumbprint, original.PackageCertificateThumbprint, original.CertificateSha256,
            original.PackageIdentity, original.Dependencies, original.MachineFiles,
            original.Files.Select(file => file.Role == InstallerPayloadFileRole.PrimaryPackage ? primaryEntry : file).ToArray());
        manifest.Validate();
        WindowsLockedPayloadFile locked = WindowsLockedPayloadFile.Open(path, entry, CancellationToken.None);
        return new WindowsInstallerReleaseLease(fixture.Request() with { InstallerPayloadSha256 = entry.Sha256 },
            manifest, fixture.RootDirectory, [locked], []);
    }
}
