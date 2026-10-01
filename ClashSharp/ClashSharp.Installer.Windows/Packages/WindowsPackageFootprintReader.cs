using System.IO.Compression;
using System.Security.Cryptography;
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

    public WindowsPackageFootprint ReadInstalled(string installedRoot, CancellationToken cancellationToken)
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
            using FileStream signature = OpenFootprint(Path.Combine(root, SignatureName));
            using FileStream blockMap = OpenFootprint(Path.Combine(root, BlockMapName));
            string signatureHash = HashBounded(signature, signature.Length, MaximumSignatureBytes, cancellationToken);
            string blockMapHash = HashBounded(blockMap, blockMap.Length, MaximumBlockMapBytes, cancellationToken);
            foreach (WindowsLockedDirectory guard in guards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                guard.Reverify();
            }
            return new(signature.Length, signatureHash, blockMap.Length, blockMapHash);
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
