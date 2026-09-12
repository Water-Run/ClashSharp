using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Windows.FileSecurity;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Transactions;

internal interface IWindowsInstallerDirectoryLedgerPersistence
{
    Task<WindowsInstallerDirectoryLedger?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(WindowsInstallerDirectoryLedger? expected, WindowsInstallerDirectoryLedger desired, CancellationToken cancellationToken);
    Task DeleteAsync(WindowsInstallerDirectoryLedger expected, CancellationToken cancellationToken);
}

internal interface IWindowsInstallerDirectoryLedgerFileNative
{
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken);
    Task PublishAsync(string path, byte[]? expected, byte[] desired, CancellationToken cancellationToken);
    Task DeleteAsync(string path, byte[] expected, CancellationToken cancellationToken);
}

internal sealed class WindowsInstallerDirectoryLedgerPersistence : IWindowsInstallerDirectoryLedgerPersistence
{
    private readonly string _path;
    private readonly Func<IDisposable> _acquireAnchor;
    private readonly IWindowsInstallerDirectoryLedgerFileNative _files;

    internal WindowsInstallerDirectoryLedgerPersistence(WindowsInstallerDirectoryCleanupLayout layout,
        Func<IDisposable> acquireAnchor, IWindowsInstallerDirectoryLedgerFileNative files)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(acquireAnchor);
        ArgumentNullException.ThrowIfNull(files);
        _path = layout.LedgerPath;
        _acquireAnchor = acquireAnchor;
        _files = files;
    }

    internal static WindowsInstallerDirectoryLedgerPersistence CreateDefault()
    {
        WindowsInstallerDirectoryCleanupLayout layout = WindowsInstallerDirectoryCleanupLayout.CreateDefault();
        return new(layout, () => WindowsInstallerDirectoryAnchor.Acquire(layout.ProgramData),
            WindowsInstallerDirectoryLedgerFileNative.Instance);
    }

    public async Task<WindowsInstallerDirectoryLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable anchor = _acquireAnchor();
        byte[]? bytes = await _files.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : WindowsInstallerDirectoryLedgerCodec.Parse(bytes);
    }

    public async Task SaveAsync(WindowsInstallerDirectoryLedger? expected, WindowsInstallerDirectoryLedger desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable anchor = _acquireAnchor();
        byte[] bytes = WindowsInstallerDirectoryLedgerCodec.Serialize(desired);
        await _files.PublishAsync(_path, expected is null ? null : WindowsInstallerDirectoryLedgerCodec.Serialize(expected),
            bytes, cancellationToken).ConfigureAwait(false);
        byte[]? observed = await _files.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
        if (observed is null || !CryptographicOperations.FixedTimeEquals(bytes, observed))
        {
            throw WindowsInstallerDirectoryLedger.Failure("write_not_observed");
        }
    }

    public async Task DeleteAsync(WindowsInstallerDirectoryLedger expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable anchor = _acquireAnchor();
        await _files.DeleteAsync(_path, WindowsInstallerDirectoryLedgerCodec.Serialize(expected), cancellationToken).ConfigureAwait(false);
        if (await _files.ReadAsync(_path, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw WindowsInstallerDirectoryLedger.Failure("delete_not_observed");
        }
    }
}

/// <summary>Only the already-existing trusted known-folder chain is pinned by this lease.</summary>
internal sealed class WindowsInstallerDirectoryAnchor : IDisposable
{
    private readonly List<WindowsDirectoryReadLease> _leases = [];

    internal static WindowsInstallerDirectoryAnchor Acquire(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)
            || !string.Equals(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase))
        {
            throw WindowsInstallerDirectoryLedger.Failure("anchor_path_invalid");
        }
        var owner = new WindowsInstallerDirectoryAnchor();
        try
        {
            string current = Path.GetPathRoot(directory) ?? throw WindowsInstallerDirectoryLedger.Failure("anchor_path_invalid");
            owner.Add(current);
            foreach (string segment in Path.GetRelativePath(current, directory).Split(Path.DirectorySeparatorChar))
            {
                if (segment is "." or ".." or "") { throw WindowsInstallerDirectoryLedger.Failure("anchor_path_invalid"); }
                current = Path.Combine(current, segment);
                owner.Add(current);
            }
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    private void Add(string path)
    {
        WindowsDirectoryReadLease lease = WindowsDirectoryReadLease.Open(path);
        _leases.Add(lease);
        WindowsDirectoryObservation observation = lease.Observe();
        if (!observation.IsDirectory || observation.IsReparsePoint
            || !WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(observation.Security))
        {
            throw WindowsInstallerDirectoryLedger.Failure("anchor_unsafe");
        }
    }

    public void Dispose()
    {
        for (int index = _leases.Count - 1; index >= 0; index--) { _leases[index].Dispose(); }
        _leases.Clear();
    }
}

internal static class WindowsInstallerDirectoryLedgerSecurity
{
    internal const string UsersSid = "S-1-5-32-545";
    private const FileSystemRights ReadRights = FileSystemRights.Read | FileSystemRights.Synchronize;

    internal static FileSecurity Create()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WindowsDirectoryAccessPolicy.AdministratorsSid));
        foreach ((string sid, FileSystemRights rights) in Rules())
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), rights, AccessControlType.Allow));
        }
        return security;
    }

    internal static void Validate(WindowsDirectorySecuritySnapshot security)
    {
        if (!security.HasDacl || !security.DaclProtected || security.OwnerSid != WindowsDirectoryAccessPolicy.AdministratorsSid
            || security.AccessEntries.Count != 3) { throw WindowsInstallerDirectoryLedger.Failure("file_acl_invalid"); }
        foreach ((string sid, FileSystemRights rights) in Rules())
        {
            WindowsDirectoryAce[] entries = security.AccessEntries.Where(entry => entry.Sid == sid).ToArray();
            if (entries.Length != 1 || entries[0].Kind != WindowsDirectoryAceKind.Allow
                || entries[0].AccessMask != (int)rights || entries[0].Flags != AceFlags.None || entries[0].IsObjectSpecific)
            {
                throw WindowsInstallerDirectoryLedger.Failure("file_acl_invalid");
            }
        }
    }

    private static (string Sid, FileSystemRights Rights)[] Rules() =>
    [
        (WindowsDirectoryAccessPolicy.LocalSystemSid, FileSystemRights.FullControl),
        (WindowsDirectoryAccessPolicy.AdministratorsSid, FileSystemRights.FullControl),
        (UsersSid, ReadRights),
    ];
}

/// <summary>Fixed ordinary file with a separately validated read-only public metadata policy.</summary>
internal sealed class WindowsInstallerDirectoryLedgerFileNative : IWindowsInstallerDirectoryLedgerFileNative
{
    internal static WindowsInstallerDirectoryLedgerFileNative Instance { get; } = new();

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle? handle = OpenIfPresent(path, deletion: false);
        return handle is null ? null : await ReadHandleAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(string path, byte[]? expected, byte[] desired, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        _ = WindowsInstallerDirectoryLedgerCodec.Parse(desired);
        cancellationToken.ThrowIfCancellationRequested();
        // A protected existing object is held open throughout publication. Delete sharing permits
        // this authority's atomic replacement; its exact DACL and the pinned parent prohibit user replacement.
        using SafeFileHandle? original = OpenIfPresent(path, deletion: false, allowDeleteSharing: true);
        byte[]? current = original is null ? null : await ReadHandleAsync(original, cancellationToken).ConfigureAwait(false);
        RequireExpected(expected, current);
        WindowsFileIdentity? originalIdentity = original is null ? null : WindowsFileSystemNative.GetOrdinaryFileIdentity(original);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + WindowsInstallerDirectoryCleanupLayout.LedgerFileName
            + "." + Guid.NewGuid().ToString("N") + ".tmp");
        WindowsFileIdentity? temporaryIdentity = null;
        bool published = false;
        try
        {
            await using (FileStream stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough, WindowsInstallerDirectoryLedgerSecurity.Create()))
            {
                ValidateFile(stream.SafeFileHandle);
                temporaryIdentity = WindowsFileSystemNative.GetOrdinaryFileIdentity(stream.SafeFileHandle);
                await stream.WriteAsync(desired, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (SafeFileHandle? final = OpenIfPresent(path, deletion: false, allowDeleteSharing: true))
            {
                if ((final is null) != (originalIdentity is null)
                    || final is not null && WindowsFileSystemNative.GetOrdinaryFileIdentity(final) != originalIdentity)
                {
                    throw WindowsInstallerDirectoryLedger.Failure("publication_target_changed");
                }
                if (final is not null)
                {
                    RequireExpected(expected, await ReadHandleAsync(final, cancellationToken).ConfigureAwait(false));
                }
            }
            // Crucially, a missing target is published WITHOUT REPLACE_EXISTING. ProgramData can
            // permit unrelated users to create names; a name won in that race must never be overwritten.
            if (!MoveFileEx(temporary, path, 8U | (originalIdentity is null ? 0U : 1U)))
            {
                throw new InstallerStateUncertainException("installer.directory_ledger.publication_uncertain");
            }
            published = true;
            using SafeFileHandle? persisted = OpenIfPresent(path, deletion: false);
            if (persisted is null || WindowsFileSystemNative.GetOrdinaryFileIdentity(persisted) != temporaryIdentity)
            {
                throw WindowsInstallerDirectoryLedger.Failure("publication_identity_changed");
            }
        }
        finally
        {
            if (!published && temporaryIdentity is { } identity) { TryDeleteTemporary(temporary, identity); }
        }
    }

    public async Task DeleteAsync(string path, byte[] expected, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle? file = OpenIfPresent(path, deletion: true);
        if (file is null) { return; }
        RequireExpected(expected, await ReadHandleAsync(file, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        DeleteHandle(file);
    }

    private static void RequireExpected(byte[]? expected, byte[]? actual)
    {
        if (actual is not null) { _ = WindowsInstallerDirectoryLedgerCodec.Parse(actual); }
        if ((expected is null) != (actual is null)
            || expected is not null && !CryptographicOperations.FixedTimeEquals(expected, actual!))
        {
            throw WindowsInstallerDirectoryLedger.Failure("compare_exchange_failed");
        }
    }

    private static async Task<byte[]> ReadHandleAsync(SafeFileHandle file, CancellationToken cancellationToken)
    {
        long size = RandomAccess.GetLength(file);
        if (size is < 1 or > WindowsInstallerDirectoryLedgerCodec.MaximumDocumentBytes)
        {
            throw WindowsInstallerDirectoryLedger.Failure("document_size_invalid");
        }
        byte[] bytes = new byte[(int)size];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = await RandomAccess.ReadAsync(file, bytes.AsMemory(offset), offset, cancellationToken).ConfigureAwait(false);
            if (count == 0) { throw WindowsInstallerDirectoryLedger.Failure("document_changed"); }
            offset += count;
        }
        if (RandomAccess.GetLength(file) != size) { throw WindowsInstallerDirectoryLedger.Failure("document_changed"); }
        return bytes;
    }

    private static SafeFileHandle? OpenIfPresent(string path, bool deletion, bool allowDeleteSharing = false)
    {
        SafeFileHandle handle = CreateFile(path, 0x80000000U | (deletion ? 0x00010000U : 0U),
            1U | (allowDeleteSharing ? 4U : 0U), 0, 3, 0x00200000U, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error == 2) { return null; }
            throw new Win32Exception(error);
        }
        try { ValidateFile(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static void ValidateFile(SafeFileHandle file)
    {
        _ = WindowsFileSystemNative.GetOrdinaryFileIdentity(file);
        if (WindowsFileSystemNative.GetLinkCount(file) != 1) { throw WindowsInstallerDirectoryLedger.Failure("file_links_invalid"); }
        WindowsInstallerDirectoryLedgerSecurity.Validate(WindowsDirectoryReadLease.ReadSecuritySnapshot(file));
    }

    private static void TryDeleteTemporary(string path, WindowsFileIdentity identity)
    {
        try
        {
            using SafeFileHandle? temporary = OpenIfPresent(path, deletion: true);
            if (temporary is not null && WindowsFileSystemNative.GetOrdinaryFileIdentity(temporary) == identity)
            {
                DeleteHandle(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InstallerProtocolException)
        {
            // Never adopt or delete a temporary from a different call, or one whose identity changed.
        }
    }

    internal static void DeleteHandle(SafeFileHandle handle)
    {
        var disposition = new FileDisposition { Delete = 1 };
        if (!SetFileInformationByHandle(handle, 4, in disposition, 1)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    }

    private static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != WindowsInstallerDirectoryCleanupLayout.LedgerFileName
            || !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            throw WindowsInstallerDirectoryLedger.Failure("path_invalid");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { internal byte Delete; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string oldPath, string newPath, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, in FileDisposition disposition, uint size);
}
