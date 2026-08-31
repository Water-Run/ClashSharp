using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

internal static class InstallerTestData
{
    internal const string Hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    internal const string OtherHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    internal const string Sid = "S-1-5-21-100-200-300-1001";
    internal const string Version = "1.2.3.4";
    internal const string TransactionId = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    internal const string CertificateThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";
    internal const string AuthenticodeThumbprint = "89ABCDEF0123456789ABCDEF0123456789ABCDEF";
    internal const string CertificateHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    internal const string DependencyHash = "1111111111111111111111111111111111111111111111111111111111111111";
    internal const string PackageName = "67dc1dc3-13fd-46c5-84f4-2932d94b566f";
    internal const string PackagePublisher = "CN=linzh";
    internal const string PackagePublisherId = "vj7sjtzkt239a";
    internal const string DependencyName = "Microsoft.WindowsAppRuntime.1.8";
    internal const string DependencyPublisher =
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    internal const string DependencyPublisherId = "8wekyb3d8bbwe";
    internal const string DependencyVersion = "8000.900.1.0";
    internal const string DependencyMinimumVersion = "8000.806.2252.0";

    internal static InstallerRequest Request(
        InstallerOperation operation = InstallerOperation.Install,
        bool allowReassociation = false,
        string version = Version,
        string hash = Hash) =>
        new(operation, Sid, allowReassociation, version, hash);

    internal static InstallerTransactionJournal Journal(
        InstallerOperation operation = InstallerOperation.Install,
        InstallerTransactionPhase phase = InstallerTransactionPhase.Prepared,
        int generation = 1) =>
        new(
            InstallerTransactionJournal.CurrentSchema,
            TransactionId,
            operation,
            Sid,
            operation == InstallerOperation.Repair,
            Version,
            Hash,
            phase,
            generation);

    internal static VerifiedInstallerRelease Release(
        bool packagePayloadAvailable = true,
        bool certificatePayloadAvailable = true,
        string installerHash = Hash,
        string certificateThumbprint = CertificateThumbprint,
        string certificateHash = CertificateHash) =>
        new(
            Version,
            installerHash,
            packagePayloadAvailable,
            certificateThumbprint,
            certificateHash,
            certificatePayloadAvailable);

    internal static TestInstallerReleaseLease Lease(
        VerifiedInstallerRelease? release = null,
        Func<InstallerRequest, CancellationToken, Task>? reverify = null,
        Func<ValueTask>? dispose = null,
        InstallerReleaseManifest? manifest = null,
        IReadOnlyList<IInstallerLockedPayloadFile>? lockedFiles = null) =>
        new(release ?? Release(), reverify, dispose, manifest, lockedFiles);

    internal static InstallerReleaseManifest Manifest(
        VerifiedInstallerRelease? release = null,
        IReadOnlyList<InstallerPayloadFileEntry>? files = null)
    {
        release ??= Release();
        return new InstallerReleaseManifest(
            InstallerReleaseManifest.CurrentSchema,
            release.ExpectedPackageVersion,
            release.InstallerPayloadSha256,
            AuthenticodeThumbprint,
            release.PackageCertificateThumbprint,
            release.CertificateSha256,
            PackageIdentity(release.ExpectedPackageVersion),
            [DependencyIdentity()],
            MachineFiles(),
            files ??
            [
                new(
                    "clashsharp_1.2.3.4_x64.msix",
                    InstallerPayloadFileRole.PrimaryPackage,
                    256,
                    release.InstallerPayloadSha256),
                new(
                    "clashsharp_temporarykey.cer",
                    InstallerPayloadFileRole.Certificate,
                    128,
                    release.CertificateSha256),
                new(
                    "dependencies/x64/microsoft.windowsappruntime.1.8.msix",
                    InstallerPayloadFileRole.DependencyPackage,
                    192,
                    DependencyHash),
                new(
                    "payload-provenance.json",
                    InstallerPayloadFileRole.Provenance,
                    96,
                    OtherHash),
            ]);
    }

    internal static InstallerMachinePayloadFileEntry[] MachineFiles() => MachinePayload()
        .Select(static entry => new InstallerMachinePayloadFileEntry(
            entry.ManifestPath,
            entry.Bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(entry.Bytes))))
        .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
        .ToArray();

    internal static (string ArchivePath, string ManifestPath, byte[] Bytes)[] MachinePayload() =>
    [
        (
            "Binaries/GeoData/ASN.mmdb",
            "binaries/geodata/asn.mmdb",
            "test-asn"u8.ToArray()),
        (
            "Binaries/GeoData/Country.mmdb",
            "binaries/geodata/country.mmdb",
            "test-country"u8.ToArray()),
        (
            "Binaries/GeoData/GeoIP.dat",
            "binaries/geodata/geoip.dat",
            "test-geoip"u8.ToArray()),
        (
            "Binaries/GeoData/GeoSite.dat",
            "binaries/geodata/geosite.dat",
            "test-geosite"u8.ToArray()),
        (
            "Binaries/GeoData/manifest.json",
            "binaries/geodata/manifest.json",
            Encoding.UTF8.GetBytes("{\"schema\":1}")),
        (
            "Binaries/mihomo.exe",
            "binaries/mihomo.exe",
            "test-mihomo"u8.ToArray()),
        (
            "Binaries/Service/ClashSharp.MihomoService.exe",
            "binaries/service/clashsharp.mihomoservice.exe",
            "test-service"u8.ToArray()),
    ];

    internal static InstallerPackageIdentity PackageIdentity(string version = Version) =>
        new(
            PackageName,
            PackagePublisher,
            PackagePublisherId,
            "x64",
            string.Empty,
            $"{PackageName}_{version}_x64__{PackagePublisherId}",
            $"{PackageName}_{PackagePublisherId}",
            "App",
            "ClashSharp.exe",
            "Windows.FullTrustApplication");

    internal static InstallerDependencyPackageIdentity DependencyIdentity(
        string path = "dependencies/x64/microsoft.windowsappruntime.1.8.msix",
        string name = DependencyName,
        string version = DependencyVersion) =>
        new(
            path,
            name,
            DependencyPublisher,
            DependencyPublisherId,
            version,
            DependencyMinimumVersion,
            "x64",
            string.Empty,
            $"{name}_{version}_x64__{DependencyPublisherId}",
            $"{name}_{DependencyPublisherId}");

    internal static InstallerCertificateOwnershipLedger CertificateLedger(
        bool wasPreExisting = false,
        int managedReferenceCount = 1,
        int generation = 1,
        string ledgerId = TransactionId,
        string targetSid = Sid,
        string certificateThumbprint = CertificateThumbprint,
        string certificateHash = CertificateHash) =>
        new(
            InstallerCertificateOwnershipLedger.CurrentSchema,
            ledgerId,
            targetSid,
            certificateThumbprint,
            certificateHash,
            InstallerCertificateStoreLocation.CurrentUser,
            InstallerCertificateStoreName.TrustedPeople,
            wasPreExisting,
            InstallerOwned: !wasPreExisting,
            managedReferenceCount,
            generation);
}

internal sealed class TestInstallerReleaseLease : IInstallerReleaseLease
{
    private readonly Func<InstallerRequest, CancellationToken, Task>? _reverify;
    private readonly Func<ValueTask>? _dispose;

    internal TestInstallerReleaseLease(
        VerifiedInstallerRelease release,
        Func<InstallerRequest, CancellationToken, Task>? reverify = null,
        Func<ValueTask>? dispose = null,
        InstallerReleaseManifest? manifest = null,
        IReadOnlyList<IInstallerLockedPayloadFile>? lockedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        Release = release;
        Manifest = manifest ?? InstallerTestData.Manifest(release);
        LockedFiles = lockedFiles ?? Manifest.Files
            .Where(file => file.Role == InstallerPayloadFileRole.Certificate
                ? release.CertificatePayloadAvailable
                : release.PackagePayloadAvailable)
            .Select(static file => (IInstallerLockedPayloadFile)new TestLockedPayloadFile(file))
            .ToArray();
        _reverify = reverify;
        _dispose = dispose;
    }

    public VerifiedInstallerRelease Release { get; }

    public InstallerReleaseManifest Manifest { get; }

    public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles { get; }

    internal bool IsDisposed { get; private set; }

    public Task ReverifyAsync(
        InstallerRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return _reverify?.Invoke(request, cancellationToken) ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        if (_dispose is not null)
        {
            await _dispose();
        }
    }
}

internal sealed class TestLockedPayloadFile : IInstallerLockedPayloadFile
{
    internal TestLockedPayloadFile(InstallerPayloadFileEntry manifestEntry)
    {
        ArgumentNullException.ThrowIfNull(manifestEntry);
        ManifestEntry = manifestEntry;
        FullPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "clashsharp-installer-payload",
            manifestEntry.Path.Replace('/', Path.DirectorySeparatorChar)));
    }

    public InstallerPayloadFileEntry ManifestEntry { get; }

    public string FullPath { get; }
}
