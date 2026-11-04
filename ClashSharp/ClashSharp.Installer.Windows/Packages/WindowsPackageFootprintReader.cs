using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Packages;

/// <summary>
/// Compares bounded footprint bytes from a signed candidate lease and pinned ordinary installed
/// directories. Every handle drains before returning; no source path is writable through this port.
/// </summary>
internal sealed class WindowsPackageFootprintReader : IWindowsPackageFootprintReader
{
    private const string SignatureName = "AppxSignature.p7x";
    private const string BlockMapName = "AppxBlockMap.xml";
    private const string BlockMapNamespace = "http://schemas.microsoft.com/appx/2010/blockmap";
    private const string BlockMap2021Namespace = "http://schemas.microsoft.com/appx/2021/blockmap";
    internal const int MaximumSignatureBytes = 4 * 1024 * 1024;
    internal const int MaximumBlockMapBytes = 16 * 1024 * 1024;

    public WindowsPackageFootprint ReadCandidate(IInstallerReleaseLease release, InstallerPayloadFileEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (release is not WindowsInstallerReleaseLease windowsLease
            || entry.Role is not (InstallerPayloadFileRole.PrimaryPackage or InstallerPayloadFileRole.DependencyPackage))
        {
            throw new InstallerProtocolException("installer.release.windows_lease_required");
        }
        using FileStream source = windowsLease.RequireFile(entry).OpenVerifiedReadStream();
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
        (long signatureLength, string signatureHash) = ReadEntry(archive, SignatureName, MaximumSignatureBytes, cancellationToken);
        (long blockMapLength, string blockMapHash) = ReadEntry(archive, BlockMapName, MaximumBlockMapBytes, cancellationToken);
        return new(signatureLength, signatureHash, blockMapLength, blockMapHash);
    }

    public WindowsPackageFootprint ReadInstalled(string installedRoot, CancellationToken cancellationToken) =>
        ObserveInstalled(installedRoot, root =>
        {
            using FileStream signature = OpenFootprint(Path.Combine(root, SignatureName));
            using FileStream blockMap = OpenFootprint(Path.Combine(root, BlockMapName));
            string signatureHash = HashBounded(signature, signature.Length, MaximumSignatureBytes, cancellationToken);
            string blockMapHash = HashBounded(blockMap, blockMap.Length, MaximumBlockMapBytes, cancellationToken);
            return new WindowsPackageFootprint(signature.Length, signatureHash, blockMap.Length, blockMapHash);
        }, cancellationToken);

    public string? ReadCandidateContent(IInstallerReleaseLease release, InstallerPayloadFileEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (release is not WindowsInstallerReleaseLease windowsLease
            || entry.Role is not (InstallerPayloadFileRole.PrimaryPackage or InstallerPayloadFileRole.DependencyPackage))
        {
            throw new InstallerProtocolException("installer.release.windows_lease_required");
        }
        using FileStream source = windowsLease.RequireFile(entry).OpenVerifiedReadStream();
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry[] entries = archive.Entries
            .Where(item => string.Equals(item.FullName, BlockMapName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].FullName != BlockMapName)
        {
            throw new InstallerProtocolException("installer.package.footprint_invalid");
        }
        using Stream blockMap = entries[0].Open();
        return DigestPayload(ReadBounded(blockMap, entries[0].Length, MaximumBlockMapBytes, cancellationToken));
    }

    public string? ReadInstalledContent(string installedRoot, CancellationToken cancellationToken) =>
        ObserveInstalled(installedRoot, root =>
        {
            using FileStream blockMap = OpenFootprint(Path.Combine(root, BlockMapName));
            return DigestPayload(ReadBounded(blockMap, blockMap.Length, MaximumBlockMapBytes, cancellationToken));
        }, cancellationToken);

    private static T ObserveInstalled<T>(string installedRoot, Func<string, T> read, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRoot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(installedRoot))
        {
            throw new InstallerProtocolException("installer.package.installed_path_invalid");
        }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installedRoot));
        var ancestors = new Stack<string>();
        var directory = new DirectoryInfo(root);
        while (directory.Parent is not null)
        {
            ancestors.Push(directory.FullName);
            directory = directory.Parent;
        }
        if (ancestors.Count == 0)
        {
            throw new InstallerProtocolException("installer.package.installed_path_invalid");
        }
        var guards = new List<WindowsLockedDirectory>();
        try
        {
            foreach (string ancestor in ancestors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                guards.Add(WindowsLockedDirectory.OpenForObservation(ancestor));
            }
            T result = read(root);
            foreach (WindowsLockedDirectory guard in guards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                guard.Reverify();
            }
            return result;
        }
        finally
        {
            for (int index = guards.Count - 1; index >= 0; index--)
            {
                guards[index].Dispose();
            }
        }
    }

    private static FileStream OpenFootprint(string path)
    {
        SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryFile(path);
        try
        {
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static (long Length, string Hash) ReadEntry(ZipArchive archive, string name, int maximumBytes,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry[] entries = archive.Entries.Where(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].FullName != name)
        {
            throw new InstallerProtocolException("installer.package.footprint_invalid");
        }
        ZipArchiveEntry entry = entries[0];
        using Stream bytes = entry.Open();
        return (entry.Length, HashBounded(bytes, entry.Length, maximumBytes, cancellationToken));
    }

    private static byte[] ReadBounded(Stream stream, long expectedLength, int maximumBytes, CancellationToken cancellationToken)
    {
        if (expectedLength <= 0 || expectedLength > maximumBytes)
        {
            throw new InstallerProtocolException("installer.package.footprint_invalid");
        }
        byte[] bytes = new byte[expectedLength];
        int length = 0;
        while (length < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(bytes, length, bytes.Length - length);
            if (count == 0)
            {
                throw new InstallerProtocolException("installer.package.footprint_invalid");
            }
            length += count;
        }
        if (stream.ReadByte() != -1)
        {
            throw new InstallerProtocolException("installer.package.footprint_invalid");
        }
        return bytes;
    }

    // Canonical payload identity: the hash method plus every file's name, size and block hashes,
    // ordered by name. Signature bytes, compressed block sizes and archive layout do not take part.
    internal static string? DigestPayload(byte[] blockMap)
    {
        ArgumentNullException.ThrowIfNull(blockMap);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumBlockMapBytes,
        };
        var files = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);
        string? hashMethod = null;
        try
        {
            using var stream = new MemoryStream(blockMap, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            StringBuilder? current = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }
                if (reader.Depth == 2 && reader.NamespaceURI == BlockMap2021Namespace && reader.LocalName == "FileHash"
                    && current is not null)
                {
                    // The 2021 schema adds a whole-file hash. The block hashes already determine every byte
                    // and packaging tools differ in whether they emit it, so it is checked but not digested.
                    if (string.IsNullOrEmpty(reader.GetAttribute("Hash")))
                    {
                        return null;
                    }
                    continue;
                }
                if (reader.NamespaceURI != BlockMapNamespace)
                {
                    return null;
                }
                if (reader.Depth == 0 && reader.LocalName == "BlockMap")
                {
                    hashMethod = reader.GetAttribute("HashMethod");
                }
                else if (reader.Depth == 1 && reader.LocalName == "File")
                {
                    string? name = reader.GetAttribute("Name");
                    string? size = reader.GetAttribute("Size");
                    if (string.IsNullOrEmpty(name) || !ulong.TryParse(size, out _) || files.ContainsKey(name))
                    {
                        return null;
                    }
                    current = new StringBuilder(size);
                    files.Add(name, current);
                }
                else if (reader.Depth == 2 && reader.LocalName == "Block" && current is not null)
                {
                    string? hash = reader.GetAttribute("Hash");
                    if (string.IsNullOrEmpty(hash))
                    {
                        return null;
                    }
                    current.Append('\n').Append(hash);
                }
                else
                {
                    return null;
                }
            }
        }
        catch (XmlException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(hashMethod) || files.Count == 0)
        {
            return null;
        }
        var canonical = new StringBuilder(hashMethod);
        foreach ((string name, StringBuilder content) in files)
        {
            canonical.Append('\0').Append(name).Append('\0').Append(content);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string HashBounded(Stream stream, long expectedLength, int maximumBytes, CancellationToken cancellationToken)
    {
        if (expectedLength <= 0 || expectedLength > maximumBytes)
        {
            throw new InstallerProtocolException("installer.package.footprint_invalid");
        }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(buffer);
            if (count == 0)
            {
                break;
            }
            length += count;
            if (length > expectedLength)
            {
                throw new InstallerProtocolException("installer.package.footprint_invalid");
            }
            hash.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (length != expectedLength)
        {
            throw new InstallerProtocolException("installer.package.footprint_invalid");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
