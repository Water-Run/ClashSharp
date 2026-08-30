using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Tests;

internal sealed class WindowsPayloadFixture : IDisposable
{
    private const string Version = "1.2.3.4";
    private readonly byte[] _certificateBytes;
    private bool _disposed;

    internal WindowsPayloadFixture(
        bool createPayload = true,
        string? primaryPackageNameOverride = null,
        bool dependencyIsFramework = true)
    {
        RootDirectory = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Installer.Windows.Tests",
            Guid.NewGuid().ToString("N"));
        PayloadRoot = Path.Combine(RootDirectory, "payload");
        ExecutablePath = Path.Combine(RootDirectory, "ClashSharp.Installer.exe");

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificateRequest = new CertificateRequest(
            $"CN=ClashSharp Installer Windows Test {Guid.NewGuid():N}",
            key,
            HashAlgorithmName.SHA256);
        using X509Certificate2 issued = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        _certificateBytes = issued.Export(X509ContentType.Cert);
        using X509Certificate2 publicCertificate = X509CertificateLoader.LoadCertificate(
            _certificateBytes);

        MachinePayloadFile[] machinePayload = CreateMachinePayload();
        byte[] primary = CreateMsix($$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
              <Identity Name="{{primaryPackageNameOverride ?? "67dc1dc3-13fd-46c5-84f4-2932d94b566f"}}"
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
            """, machinePayload);
        byte[] dependency = CreateMsix($$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Microsoft.WindowsAppRuntime.1.8"
                        Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
                        Version="8000.900.1.0"
                        ProcessorArchitecture="x64" />
              <Properties><Framework>{{dependencyIsFramework.ToString().ToLowerInvariant()}}</Framework></Properties>
            </Package>
            """);
        byte[] provenance = "{\"schema\":1}"u8.ToArray();
        Manifest = new InstallerReleaseManifest(
            InstallerReleaseManifest.CurrentSchema,
            Version,
            Sha256(primary),
            Convert.ToHexString(publicCertificate.GetCertHash(HashAlgorithmName.SHA1)),
            Sha256(_certificateBytes),
            new InstallerPackageIdentity(
                "67dc1dc3-13fd-46c5-84f4-2932d94b566f",
                "CN=linzh",
                "vj7sjtzkt239a",
                "x64",
                string.Empty,
                "67dc1dc3-13fd-46c5-84f4-2932d94b566f_1.2.3.4_x64__vj7sjtzkt239a",
                "67dc1dc3-13fd-46c5-84f4-2932d94b566f_vj7sjtzkt239a",
                "App",
                "ClashSharp.exe",
                "Windows.FullTrustApplication"),
            [
                new InstallerDependencyPackageIdentity(
                    "dependencies/x64/microsoft.windowsappruntime.1.8.msix",
                    "Microsoft.WindowsAppRuntime.1.8",
                    "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
                    "8wekyb3d8bbwe",
                    "8000.900.1.0",
                    "8000.806.2252.0",
                    "x64",
                    string.Empty,
                    "Microsoft.WindowsAppRuntime.1.8_8000.900.1.0_x64__8wekyb3d8bbwe",
                    "Microsoft.WindowsAppRuntime.1.8_8wekyb3d8bbwe"),
            ],
            machinePayload
                .Select(static file => new InstallerMachinePayloadFileEntry(
                    file.ManifestPath,
                    file.Bytes.LongLength,
                    Sha256(file.Bytes)))
                .OrderBy(static file => file.Path, StringComparer.Ordinal)
                .ToArray(),
            [
                Entry(
                    "clashsharp_1.2.3.4_x64.msix",
                    InstallerPayloadFileRole.PrimaryPackage,
                    primary),
                Entry(
                    "clashsharp_temporarykey.cer",
                    InstallerPayloadFileRole.Certificate,
                    _certificateBytes),
                Entry(
                    "dependencies/x64/microsoft.windowsappruntime.1.8.msix",
                    InstallerPayloadFileRole.DependencyPackage,
                    dependency),
                Entry(
                    "payload-provenance.json",
                    InstallerPayloadFileRole.Provenance,
                    provenance),
            ]);
        Manifest.Validate();

        if (createPayload)
        {
            Directory.CreateDirectory(Path.Combine(PayloadRoot, "dependencies", "x64"));
            File.WriteAllBytes(PrimaryPath, primary);
            File.WriteAllBytes(CertificatePath, _certificateBytes);
            File.WriteAllBytes(DependencyPath, dependency);
            File.WriteAllBytes(ProvenancePath, provenance);
        }
        else
        {
            Directory.CreateDirectory(RootDirectory);
        }
    }

    internal string RootDirectory { get; }

    internal string PayloadRoot { get; }

    internal string ExecutablePath { get; }

    internal string PrimaryPath => Path.Combine(PayloadRoot, "clashsharp_1.2.3.4_x64.msix");

    internal string CertificatePath => Path.Combine(PayloadRoot, "clashsharp_temporarykey.cer");

    internal string DependencyPath => Path.Combine(
        PayloadRoot,
        "dependencies",
        "x64",
        "microsoft.windowsappruntime.1.8.msix");

    internal string ProvenancePath => Path.Combine(PayloadRoot, "payload-provenance.json");

    internal InstallerReleaseManifest Manifest { get; }

    internal byte[] ManifestBytes => InstallerReleaseManifestCodec.Serialize(Manifest);

    internal InstallerRequest Request(
        InstallerOperation operation = InstallerOperation.Install,
        string? targetSid = null) =>
        new(
            operation,
            targetSid ?? CurrentSid(),
            AllowReassociation: false,
            Manifest.ExpectedPackageVersion,
            Manifest.InstallerPayloadSha256);

    internal WindowsInstallerReleaseLease Lock(InstallerRequest? request = null) =>
        WindowsInstallerPayloadLocker.Lock(
            request ?? Request(),
            Manifest,
            PayloadRoot,
            CancellationToken.None);

    internal static void AssertWindows11X64()
    {
        Assert.True(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
        Assert.True(Environment.Is64BitOperatingSystem);
        Assert.True(Environment.Is64BitProcess);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            RemoveTestCertificate();
        }

        if (Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }

        CryptographicOperations.ZeroMemory(_certificateBytes);
        _disposed = true;
    }

    private static InstallerPayloadFileEntry Entry(
        string path,
        InstallerPayloadFileRole role,
        byte[] bytes) =>
        new(path, role, bytes.LongLength, Sha256(bytes));

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] CreateMsix(
        string manifest,
        IReadOnlyList<MachinePayloadFile>? machinePayload = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "AppxManifest.xml", Encoding.UTF8.GetBytes(manifest));
            WriteEntry(archive, "AppxBlockMap.xml", "<BlockMap />"u8);
            WriteEntry(archive, "AppxSignature.p7x", [0x01]);
            if (machinePayload is not null)
            {
                foreach (MachinePayloadFile file in machinePayload)
                {
                    WriteEntry(archive, file.ArchivePath, file.Bytes);
                }
            }
        }

        return output.ToArray();
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

    private static MachinePayloadFile[] CreateMachinePayload() =>
    [
        new(
            "Binaries/GeoData/ASN.mmdb",
            "binaries/geodata/asn.mmdb",
            "test-asn"u8.ToArray()),
        new(
            "Binaries/GeoData/Country.mmdb",
            "binaries/geodata/country.mmdb",
            "test-country"u8.ToArray()),
        new(
            "Binaries/GeoData/GeoIP.dat",
            "binaries/geodata/geoip.dat",
            "test-geoip"u8.ToArray()),
        new(
            "Binaries/GeoData/GeoSite.dat",
            "binaries/geodata/geosite.dat",
            "test-geosite"u8.ToArray()),
        new(
            "Binaries/GeoData/manifest.json",
            "binaries/geodata/manifest.json",
            "{\"schema\":1}"u8.ToArray()),
        new(
            "Binaries/mihomo.exe",
            "binaries/mihomo.exe",
            "test-mihomo"u8.ToArray()),
        new(
            "Binaries/Service/ClashSharp.MihomoService.exe",
            "binaries/service/clashsharp.mihomoservice.exe",
            "test-service"u8.ToArray()),
    ];

    private static string CurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value
            ?? throw new InvalidOperationException("The Windows test user SID is unavailable.");
    }

    private sealed record MachinePayloadFile(
        string ArchivePath,
        string ManifestPath,
        byte[] Bytes);

    private void RemoveTestCertificate()
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        X509Certificate2Collection certificates = store.Certificates;
        try
        {
            foreach (X509Certificate2 certificate in certificates)
            {
                if (string.Equals(
                        Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA1)),
                        Manifest.PackageCertificateThumbprint,
                        StringComparison.Ordinal)
                    && SHA256.HashData(certificate.RawData).AsSpan()
                        .SequenceEqual(SHA256.HashData(_certificateBytes)))
                {
                    store.Remove(certificate);
                }
            }
        }
        finally
        {
            foreach (X509Certificate2 certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }
}

internal sealed class FatalTestException : OutOfMemoryException
{
    internal FatalTestException(string message)
        : base(message)
    {
    }
}
