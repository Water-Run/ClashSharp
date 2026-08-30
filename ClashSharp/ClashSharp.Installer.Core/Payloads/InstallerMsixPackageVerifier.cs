using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Payloads;

/// <summary>
/// Validates bounded MSIX metadata and machine payload against an embedded release manifest.
/// </summary>
public static class InstallerMsixPackageVerifier
{
    private const string AppxManifestPath = "AppxManifest.xml";
    private const string AppxBlockMapPath = "AppxBlockMap.xml";
    private const string AppxSignaturePath = "AppxSignature.p7x";
    private static readonly XNamespace FoundationNamespace =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap10Namespace =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Validates primary package identity, application identity, integrity enforcement, and
    /// dependency requirements, and exact machine-file hashes. The caller retains ownership of
    /// <paramref name="packageStream"/>.
    /// </summary>
    public static void VerifyPrimary(
        Stream packageStream,
        InstallerReleaseManifest release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        release.Validate();
        VerifyBounded(
            packageStream,
            (archive, document) =>
            {
                VerifyPrimaryManifest(document, release);
                VerifyMachineFiles(archive, release.MachineFiles, cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// Validates one dependency package as the exact x64 framework identity in the embedded
    /// manifest. The caller retains ownership of <paramref name="packageStream"/>.
    /// </summary>
    public static void VerifyDependency(
        Stream packageStream,
        InstallerDependencyPackageIdentity expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        VerifyBounded(
            packageStream,
            (_, document) => VerifyDependencyManifest(document, expected),
            cancellationToken);
    }

    private static void VerifyBounded(
        Stream packageStream,
        Action<ZipArchive, XDocument> verifyManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        cancellationToken.ThrowIfCancellationRequested();
        if (!packageStream.CanRead || !packageStream.CanSeek)
        {
            throw new InstallerProtocolException("installer.release.package_archive_invalid");
        }

        try
        {
            packageStream.Position = 0;
            using var archive = new ZipArchive(
                packageStream,
                ZipArchiveMode.Read,
                leaveOpen: true);
            ValidateArchive(archive, cancellationToken);
            XDocument document = ReadManifest(archive, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            verifyManifest(archive, document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or NotSupportedException
            or UnauthorizedAccessException
            or DecoderFallbackException
            or XmlException)
        {
            throw new InstallerProtocolException(
                "installer.release.package_metadata_invalid",
                exception);
        }
    }

    private static void ValidateArchive(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count is < 3 or > InstallerPayloadBudgets.MaximumPackageArchiveEntries)
        {
            throw new InstallerProtocolException("installer.release.package_archive_invalid");
        }

        var observedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = entry.FullName;
            if (path.Length is < 1 or > 512
                || path[0] == '/'
                || path[^1] == '/'
                || path.Contains('\\')
                || path.Contains("//", StringComparison.Ordinal)
                || path.Split('/').Any(static segment => segment is "." or ".."
                    || segment.EndsWith('.')
                    || segment.EndsWith(' '))
                || entry.Length <= 0
                || !observedPaths.Add(path))
            {
                throw new InstallerProtocolException("installer.release.package_archive_invalid");
            }

            try
            {
                expandedBytes = checked(expandedBytes + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw new InstallerProtocolException(
                    "installer.release.package_archive_invalid",
                    exception);
            }

            if (expandedBytes > InstallerPayloadBudgets.MaximumExpandedPackageBytes)
            {
                throw new InstallerProtocolException("installer.release.package_archive_invalid");
            }
        }
    }

    private static XDocument ReadManifest(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry manifestEntry = RequireCanonicalEntry(
            archive,
            AppxManifestPath,
            InstallerPayloadBudgets.MaximumAppxManifestBytes);
        _ = RequireCanonicalEntry(
            archive,
            AppxBlockMapPath,
            InstallerPayloadBudgets.MaximumAppxBlockMapBytes);
        _ = RequireCanonicalEntry(
            archive,
            AppxSignaturePath,
            InstallerPayloadBudgets.MaximumAppxSignatureBytes);

        byte[] bytes = ReadExactEntry(manifestEntry, cancellationToken);
        try
        {
            ReadOnlySpan<byte> xmlBytes = bytes.AsSpan();
            if (xmlBytes.Length >= 3
                && xmlBytes[0] == 0xEF
                && xmlBytes[1] == 0xBB
                && xmlBytes[2] == 0xBF)
            {
                xmlBytes = xmlBytes[3..];
            }

            string xml = StrictUtf8.GetString(xmlBytes);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = false,
                IgnoreWhitespace = false,
                MaxCharactersInDocument = InstallerPayloadBudgets.MaximumAppxManifestBytes,
                XmlResolver = null,
            };
            using var text = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(text, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        finally
        {
            bytes.AsSpan().Clear();
        }
    }

    private static void VerifyMachineFiles(
        ZipArchive archive,
        IReadOnlyList<InstallerMachinePayloadFileEntry> expectedFiles,
        CancellationToken cancellationToken)
    {
        Dictionary<string, InstallerMachinePayloadFileEntry> expectedByPath = expectedFiles
            .ToDictionary(static file => file.Path, StringComparer.Ordinal);
        var observedByPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = entry.FullName.ToLowerInvariant();
            if (!IsMachineScopePath(normalizedPath))
            {
                continue;
            }

            if (!expectedByPath.ContainsKey(normalizedPath)
                || !observedByPath.TryAdd(normalizedPath, entry))
            {
                throw new InstallerProtocolException(
                    "installer.release.machine_file_set_invalid");
            }
        }

        if (observedByPath.Count != expectedByPath.Count)
        {
            throw new InstallerProtocolException(
                "installer.release.machine_file_set_invalid");
        }

        byte[] buffer = GC.AllocateUninitializedArray<byte>(64 * 1024);
        try
        {
            foreach (InstallerMachinePayloadFileEntry expected in expectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = observedByPath[expected.Path];
                if (entry.Length != expected.Length)
                {
                    throw new InstallerProtocolException(
                        "installer.release.machine_file_mismatch");
                }

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using Stream stream = entry.Open();
                long actualLength = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = stream.Read(buffer);
                    if (read == 0)
                    {
                        break;
                    }

                    actualLength = checked(actualLength + read);
                    if (actualLength > expected.Length)
                    {
                        throw new InstallerProtocolException(
                            "installer.release.machine_file_mismatch");
                    }

                    hasher.AppendData(buffer.AsSpan(0, read));
                }

                byte[] digest = hasher.GetHashAndReset();
                try
                {
                    if (actualLength != expected.Length
                        || !string.Equals(
                            Convert.ToHexStringLower(digest),
                            expected.Sha256,
                            StringComparison.Ordinal))
                    {
                        throw new InstallerProtocolException(
                            "installer.release.machine_file_mismatch");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(digest);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool IsMachineScopePath(string normalizedPath) =>
        string.Equals(normalizedPath, "binaries/mihomo.exe", StringComparison.Ordinal)
        || normalizedPath.StartsWith("binaries/service/", StringComparison.Ordinal)
        || normalizedPath.StartsWith("binaries/geodata/", StringComparison.Ordinal);

    private static ZipArchiveEntry RequireCanonicalEntry(
        ZipArchive archive,
        string canonicalPath,
        long maximumLength)
    {
        ZipArchiveEntry[] matches = archive.Entries
            .Where(entry => string.Equals(
                entry.FullName,
                canonicalPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1
            || !string.Equals(matches[0].FullName, canonicalPath, StringComparison.Ordinal)
            || matches[0].Length is <= 0
            || matches[0].Length > maximumLength)
        {
            throw new InstallerProtocolException("installer.release.package_archive_invalid");
        }

        return matches[0];
    }

    private static byte[] ReadExactEntry(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length > int.MaxValue)
        {
            throw new InstallerProtocolException("installer.release.package_archive_invalid");
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)entry.Length));
        using Stream stream = entry.Open();
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(bytes.AsSpan(offset));
            if (read == 0)
            {
                throw new InstallerProtocolException("installer.release.package_archive_invalid");
            }

            offset = checked(offset + read);
        }

        if (stream.ReadByte() != -1)
        {
            throw new InstallerProtocolException("installer.release.package_archive_invalid");
        }

        return bytes;
    }

    private static void VerifyPrimaryManifest(
        XDocument document,
        InstallerReleaseManifest release)
    {
        XElement package = RequireRootPackage(document);
        XElement identity = RequireDirectChild(package, FoundationNamespace + "Identity");
        InstallerPackageIdentity expected = release.PackageIdentity;
        if (!MatchesAttribute(identity, "Name", expected.Name)
            || !MatchesAttribute(identity, "Publisher", expected.Publisher)
            || !MatchesAttribute(identity, "Version", release.ExpectedPackageVersion)
            || !MatchesAttribute(identity, "ProcessorArchitecture", expected.Architecture)
            || !MatchesOptionalAttribute(identity, "ResourceId", expected.ResourceId))
        {
            throw new InstallerProtocolException(
                "installer.release.package_identity_mismatch");
        }

        XElement applications = RequireDirectChild(
            package,
            FoundationNamespace + "Applications");
        XElement application = RequireDirectChild(
            applications,
            FoundationNamespace + "Application");
        if (!MatchesAttribute(application, "Id", expected.ApplicationId)
            || !MatchesAttribute(application, "Executable", expected.ApplicationExecutable)
            || !MatchesAttribute(application, "EntryPoint", expected.ApplicationEntryPoint))
        {
            throw new InstallerProtocolException(
                "installer.release.package_identity_mismatch");
        }

        XElement properties = RequireDirectChild(
            package,
            FoundationNamespace + "Properties");
        XElement integrity = RequireDirectChild(properties, Uap10Namespace + "PackageIntegrity");
        XElement content = RequireDirectChild(integrity, Uap10Namespace + "Content");
        if (!MatchesAttribute(content, "Enforcement", "on"))
        {
            throw new InstallerProtocolException(
                "installer.release.package_integrity_contract_invalid");
        }

        XElement dependencies = RequireDirectChild(
            package,
            FoundationNamespace + "Dependencies");
        XElement[] declared = dependencies.Elements(FoundationNamespace + "PackageDependency")
            .ToArray();
        if (declared.Length != release.Dependencies.Count)
        {
            throw new InstallerProtocolException(
                "installer.release.package_dependency_contract_invalid");
        }

        foreach (InstallerDependencyPackageIdentity expectedDependency in release.Dependencies)
        {
            int matches = declared.Count(element =>
                MatchesAttribute(element, "Name", expectedDependency.Name)
                && MatchesAttribute(element, "Publisher", expectedDependency.Publisher)
                && MatchesAttribute(
                    element,
                    "MinVersion",
                    expectedDependency.MinimumVersion));
            if (matches != 1)
            {
                throw new InstallerProtocolException(
                    "installer.release.package_dependency_contract_invalid");
            }
        }
    }

    private static void VerifyDependencyManifest(
        XDocument document,
        InstallerDependencyPackageIdentity expected)
    {
        XElement package = RequireRootPackage(document);
        XElement identity = RequireDirectChild(package, FoundationNamespace + "Identity");
        if (!MatchesAttribute(identity, "Name", expected.Name)
            || !MatchesAttribute(identity, "Publisher", expected.Publisher)
            || !MatchesAttribute(identity, "Version", expected.Version)
            || !MatchesAttribute(identity, "ProcessorArchitecture", expected.Architecture)
            || !MatchesOptionalAttribute(identity, "ResourceId", expected.ResourceId))
        {
            throw new InstallerProtocolException(
                "installer.release.dependency_identity_mismatch");
        }

        XElement properties = RequireDirectChild(
            package,
            FoundationNamespace + "Properties");
        XElement framework = RequireDirectChild(
            properties,
            FoundationNamespace + "Framework");
        if (!string.Equals(framework.Value.Trim(), "true", StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.release.dependency_identity_mismatch");
        }
    }

    private static XElement RequireRootPackage(XDocument document)
    {
        XElement root = document.Root
            ?? throw new InstallerProtocolException("installer.release.package_metadata_invalid");
        if (root.Name != FoundationNamespace + "Package")
        {
            throw new InstallerProtocolException("installer.release.package_metadata_invalid");
        }

        return root;
    }

    private static XElement RequireDirectChild(XElement parent, XName name)
    {
        XElement[] matches = parent.Elements(name).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InstallerProtocolException("installer.release.package_metadata_invalid");
    }

    private static bool MatchesAttribute(
        XElement element,
        XName name,
        string expected) =>
        string.Equals((string?)element.Attribute(name), expected, StringComparison.Ordinal);

    private static bool MatchesOptionalAttribute(
        XElement element,
        XName name,
        string expected) =>
        string.Equals((string?)element.Attribute(name) ?? string.Empty, expected, StringComparison.Ordinal);
}
