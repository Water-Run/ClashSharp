using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Pins every ordinary original-package file against writing or deletion and every directory
/// against substitution. The bounded canonical digest includes relative names, lengths and bytes;
/// the tree is independently enumerated again to reject extra entries. Disposal releases all pins.
/// </summary>
internal sealed class WindowsMaintenancePackageStateLease : IDisposable
{
    private readonly string _root;
    private readonly List<WindowsLockedDirectory> _directories;
    private readonly List<LockedFile> _files;
    private readonly HashSet<string> _entries;
    private bool _disposed;

    private WindowsMaintenancePackageStateLease(string root, List<WindowsLockedDirectory> directories,
        List<LockedFile> files, HashSet<string> entries)
    {
        _root = root;
        _directories = directories;
        _files = files;
        _entries = entries;
        ContentsSha256 = Digest(files, entries);
    }

    internal string ContentsSha256 { get; }

    internal static WindowsMaintenancePackageStateLease Acquire(string packageRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(packageRoot)) { throw Failure("path_invalid"); }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        if (Path.GetPathRoot(root) == root) { throw Failure("path_invalid"); }
        var directories = new List<WindowsLockedDirectory>();
        var files = new List<LockedFile>();
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool returned = false;
        try
        {
            var ancestors = new Stack<string>();
            var ancestor = new DirectoryInfo(root);
            while (ancestor.Parent is not null) { ancestors.Push(ancestor.FullName); ancestor = ancestor.Parent; }
            foreach (string path in ancestors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                directories.Add(WindowsLockedDirectory.OpenForObservation(path));
            }
            long totalBytes = 0;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = Relative(root, path);
                    if (!entries.Add(relative) || entries.Count > InstallerPayloadBudgets.MaximumPackageArchiveEntries)
                    {
                        throw Failure("entry_budget_invalid");
                    }
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { throw Failure("entry_invalid"); }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Add(WindowsLockedDirectory.OpenForObservation(path));
                        pending.Push(path);
                        continue;
                    }
                    SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryFile(path);
                    bool retained = false;
                    try
                    {
                        long length = RandomAccess.GetLength(handle);
                        if (length < 0 || length > InstallerPayloadBudgets.MaximumFileBytes
                            || length > InstallerPayloadBudgets.MaximumExpandedPackageBytes - totalBytes)
                        {
                            throw Failure("byte_budget_invalid");
                        }
                        totalBytes += length;
                        WindowsFileIdentity identity = WindowsFileSystemNative.GetOrdinaryFileIdentity(handle);
                        files.Add(new(path, relative, handle, identity, length, Hash(handle, length, cancellationToken)));
                        retained = true;
                    }
                    finally { if (!retained) { handle.Dispose(); } }
                }
            }
            if (files.Count == 0) { throw Failure("empty"); }
            var lease = new WindowsMaintenancePackageStateLease(root, directories, files, entries);
            lease.Reverify(cancellationToken);
            returned = true;
            return lease;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.recovery.original_package_contents_inspection_failed", exception);
        }
        finally
        {
            if (!returned) { Release(files, directories); }
        }
    }

    internal void Reverify(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (WindowsLockedDirectory directory in _directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                directory.Reverify();
            }
            var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(_root);
            while (pending.Count > 0)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = Relative(_root, path);
                    if (!_entries.Contains(relative) || !observed.Add(relative)) { throw Failure("changed"); }
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { throw Failure("changed"); }
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); }
                }
            }
            if (!observed.SetEquals(_entries)) { throw Failure("changed"); }
            foreach (LockedFile file in _files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using SafeFileHandle probe = WindowsFileSystemNative.OpenOrdinaryFile(file.Path);
                if (WindowsFileSystemNative.GetOrdinaryFileIdentity(probe) != file.Identity
                    || WindowsFileSystemNative.GetOrdinaryFileIdentity(file.Handle) != file.Identity
                    || RandomAccess.GetLength(file.Handle) != file.Length
                    || Hash(file.Handle, file.Length, cancellationToken) != file.Sha256)
                {
                    throw Failure("changed");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.recovery.original_package_contents_inspection_failed", exception);
        }
    }

    private static string Relative(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.Length > InstallerPayloadBudgets.MaximumRelativePathCharacters
            || relative.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw Failure("path_invalid");
        }
        return relative.ToLowerInvariant();
    }

    private static string Digest(List<LockedFile> files, HashSet<string> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("ClashSharp.OriginalPackageContents.v1\0"u8);
        Span<byte> size = stackalloc byte[sizeof(long)];
        Dictionary<string, LockedFile> byPath = files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        foreach (string path in entries.Order(StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes(path);
            BinaryPrimitives.WriteInt32LittleEndian(size, name.Length);
            hash.AppendData(size[..sizeof(int)]);
            hash.AppendData(name);
            if (byPath.TryGetValue(path, out LockedFile? file))
            {
                hash.AppendData("F"u8);
                BinaryPrimitives.WriteInt64LittleEndian(size, file.Length);
                hash.AppendData(size);
                hash.AppendData(Convert.FromHexString(file.Sha256));
            }
            else { hash.AppendData("D"u8); }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Hash(SafeFileHandle handle, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                if (count <= 0) { throw Failure("changed"); }
                hash.AppendData(buffer.AsSpan(0, count));
                offset += count;
            }
            if (RandomAccess.GetLength(handle) != length) { throw Failure("changed"); }
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); ArrayPool<byte>.Shared.Return(buffer); }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        Release(_files, _directories);
    }

    private static void Release(List<LockedFile> files, List<WindowsLockedDirectory> directories)
    {
        for (int index = files.Count - 1; index >= 0; index--) { files[index].Handle.Dispose(); }
        for (int index = directories.Count - 1; index >= 0; index--) { directories[index].Dispose(); }
    }
    private static InstallerProtocolException Failure(string suffix) => new("installer.recovery.original_package_contents_" + suffix);
    private sealed record LockedFile(string Path, string RelativePath, SafeFileHandle Handle, WindowsFileIdentity Identity, long Length, string Sha256);
}
