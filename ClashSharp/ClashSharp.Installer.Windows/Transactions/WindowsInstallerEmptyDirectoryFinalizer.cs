using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Windows.FileSecurity;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Transactions;

internal interface IWindowsInstallerDirectoryDeletionLease : IDisposable
{
    WindowsFileIdentity Identity { get; }
    bool IsEmpty { get; }
    void VerifyOwnedSecurity();
    void DeleteEmpty();
}

internal interface IWindowsInstallerEmptyDirectoryNative
{
    IWindowsInstallerDirectoryDeletionLease? Open(string path, CancellationToken cancellationToken);
}

/// <summary>Runs after Clear reconciliation, before a success frame, under both session-wide leases.</summary>
internal sealed class WindowsInstallerEmptyDirectoryFinalizer
{
    private readonly WindowsInstallerDirectoryCleanupLayout _layout;
    private readonly IWindowsInstallerDirectoryLedgerPersistence _ledger;
    private readonly IWindowsInstallerEmptyDirectoryNative _native;

    internal WindowsInstallerEmptyDirectoryFinalizer(WindowsInstallerDirectoryCleanupLayout layout,
        IWindowsInstallerDirectoryLedgerPersistence ledger, IWindowsInstallerEmptyDirectoryNative native)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(native);
        _layout = layout;
        _ledger = ledger;
        _native = native;
    }

    internal static WindowsInstallerEmptyDirectoryFinalizer CreateDefault() => new(
        WindowsInstallerDirectoryCleanupLayout.CreateDefault(), WindowsInstallerDirectoryLedgerPersistence.CreateDefault(),
        WindowsInstallerEmptyDirectoryNative.Instance);

    internal async Task<InstallerDirectoryCleanupReport> CompleteAsync(InstallerTransactionSnapshot verified,
        Func<ValueTask> releaseResources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(releaseResources);
        verified.Validate();
        if (verified.Journal.Operation != InstallerOperation.Uninstall || verified.Journal.Phase != InstallerTransactionPhase.Verified)
        {
            throw WindowsInstallerDirectoryLedger.Failure("terminal_not_verified_uninstall");
        }
        WindowsInstallerDirectoryLedger? before = await _ledger.LoadAsync(cancellationToken).ConfigureAwait(false);
        WindowsInstallerDirectoryLedger prepared = (before ?? WindowsInstallerDirectoryLedger.Empty).BeginTerminal(verified);
        if (before?.Terminal is null)
        {
            // A committed Clear replay may have no ledger, or only newly recreated directory IDs.
            // Its authoritative native verification already succeeded; arm the same checkpoint again.
            await _ledger.SaveAsync(before, prepared, cancellationToken).ConfigureAwait(false);
        }
        WindowsInstallerDirectoryLedger current = await _ledger.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw WindowsInstallerDirectoryLedger.Failure("terminal_missing");
        RequireExact(prepared, current);

        // This releases machine/user certificate persistence and transaction guards. It does not
        // release exclusive installer authority or the application lease owned by the outer host.
        await releaseResources().ConfigureAwait(false);
        var entries = new List<InstallerDirectoryCleanupEntry>(6);
        foreach (InstallerDirectoryRole role in WindowsInstallerDirectoryCleanupLayout.DeletionOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsInstallerOwnedDirectory? owned = current.Directories.SingleOrDefault(entry => entry.Role == role);
            InstallerDirectoryCleanupDisposition disposition = RemoveOne(_layout.GetPath(role), owned, cancellationToken);
            entries.Add(new(role, disposition));
        }
        var report = new InstallerDirectoryCleanupReport(entries.OrderBy(entry => entry.Role));
        report.Validate();
        // This is the final durable mutation. Any preceding cancellation, exception or process exit
        // retains the verified checkpoint outside the directories being removed.
        await _ledger.DeleteAsync(current, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private InstallerDirectoryCleanupDisposition RemoveOne(string path, WindowsInstallerOwnedDirectory? owned,
        CancellationToken cancellationToken)
    {
        Exception? deletionFailure = null;
        using (IWindowsInstallerDirectoryDeletionLease? directory = _native.Open(path, cancellationToken))
        {
            if (directory is null) { return InstallerDirectoryCleanupDisposition.Missing; }
            if (owned is null || owned.Identity != directory.Identity)
            {
                return InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership;
            }
            directory.VerifyOwnedSecurity();
            if (!directory.IsEmpty) { return InstallerDirectoryCleanupDisposition.RetainedNonEmpty; }
            cancellationToken.ThrowIfCancellationRequested();
            try { directory.DeleteEmpty(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            {
                deletionFailure = exception;
            }
        }
        using IWindowsInstallerDirectoryDeletionLease? after = _native.Open(path, cancellationToken);
        if (after is null) { return InstallerDirectoryCleanupDisposition.Deleted; }
        if (owned.Identity != after.Identity) { return InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership; }
        after.VerifyOwnedSecurity();
        if (!after.IsEmpty) { return InstallerDirectoryCleanupDisposition.RetainedNonEmpty; }
        if (deletionFailure is not null)
        {
            throw new InstallerProtocolException("installer.directory_ledger.directory_delete_failed", deletionFailure);
        }
        throw new InstallerStateUncertainException("installer.directory_ledger.directory_delete_not_observed");
    }

    private static void RequireExact(WindowsInstallerDirectoryLedger expected, WindowsInstallerDirectoryLedger actual)
    {
        if (!WindowsInstallerDirectoryLedgerCodec.Serialize(expected).AsSpan()
            .SequenceEqual(WindowsInstallerDirectoryLedgerCodec.Serialize(actual)))
        {
            throw WindowsInstallerDirectoryLedger.Failure("terminal_changed");
        }
    }
}

internal sealed class WindowsInstallerEmptyDirectoryNative : IWindowsInstallerEmptyDirectoryNative
{
    internal static WindowsInstallerEmptyDirectoryNative Instance { get; } = new();

    public IWindowsInstallerDirectoryDeletionLease? Open(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsInstallerDirectoryAnchor? anchor = null;
        SafeFileHandle? handle = null;
        try
        {
            anchor = WindowsInstallerDirectoryAnchor.Acquire(Path.GetDirectoryName(path)
                ?? throw WindowsInstallerDirectoryLedger.Failure("directory_path_invalid"));
            // Read-only observation first. DELETE access is acquired only after ownership is proven.
            handle = CreateFile(path, 0x80000000U, 3, 0, 3, 0x02200000U, 0);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                handle = null;
                throw new Win32Exception(error);
            }
            WindowsFileIdentity identity = WindowsFileSystemNative.GetOrdinaryDirectoryIdentity(handle);
            return new Lease(path, handle, anchor, identity);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            handle?.Dispose();
            anchor?.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            handle?.Dispose();
            anchor?.Dispose();
            return null;
        }
        catch { handle?.Dispose(); anchor?.Dispose(); throw; }
    }

    private sealed class Lease(string path, SafeFileHandle handle, WindowsInstallerDirectoryAnchor anchor,
        WindowsFileIdentity identity) : IWindowsInstallerDirectoryDeletionLease
    {
        private SafeFileHandle? _handle = handle;
        public WindowsFileIdentity Identity => identity;

        public bool IsEmpty
        {
            get
            {
                ObjectDisposedException.ThrowIf(_handle is null, this);
                using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                return !entries.MoveNext() && !HasNamedDataStream(path);
            }
        }

        public void VerifyOwnedSecurity()
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            if (WindowsFileSystemNative.GetOrdinaryDirectoryIdentity(_handle) != identity
                || !WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(WindowsDirectoryReadLease.ReadSecuritySnapshot(_handle)))
            {
                throw WindowsInstallerDirectoryLedger.Failure("owned_directory_unsafe");
            }
        }

        public void DeleteEmpty()
        {
            VerifyOwnedSecurity();
            if (!IsEmpty) { throw new IOException("The owned directory is no longer empty."); }
            // Reopen DELETE while the parent chain remains pinned. The current read handle must be
            // released because its list access intentionally withholds delete sharing.
            _handle!.Dispose();
            _handle = null;
            SafeFileHandle deleting = CreateFile(path, 0x80010000U, 3, 0, 3, 0x02200000U, 0);
            if (deleting.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                deleting.Dispose();
                throw new Win32Exception(error);
            }
            _handle = deleting;
            VerifyOwnedSecurity();
            if (!IsEmpty) { throw new IOException("The owned directory is no longer empty."); }
            WindowsInstallerDirectoryLedgerFileNative.DeleteHandle(deleting);
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
            anchor.Dispose();
        }
    }

    private static bool HasNamedDataStream(string path)
    {
        nint search = FindFirstStream(path, 0, out _, 0);
        if (search != new nint(-1))
        {
            _ = FindClose(search);
            return true;
        }
        int error = Marshal.GetLastPInvokeError();
        if (error == 38) { return false; }
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        internal long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] internal string StreamName;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint FindFirstStream(string name, int informationLevel, out StreamData data, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint search);
}
