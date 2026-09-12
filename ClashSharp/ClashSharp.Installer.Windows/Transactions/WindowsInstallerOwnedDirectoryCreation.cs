using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Windows.FileSecurity;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Transactions;

internal interface IWindowsInstallerOwnedDirectoryCreationNative
{
    bool CreateDirectory(string path, DirectorySecurity security);
    IWindowsInstallerCreatedDirectoryLease OpenCreatedDirectory(string path);
}

internal interface IWindowsInstallerCreatedDirectoryLease : IDisposable
{
    WindowsFileIdentity Identity { get; }
}

/// <summary>Never adopts an existing directory, including an unrecorded creation from an interrupted call.</summary>
internal sealed class WindowsInstallerOwnedDirectoryCreation
{
    private readonly WindowsInstallerDirectoryCleanupLayout _layout;
    private readonly IWindowsInstallerDirectoryLedgerPersistence _ledger;
    private readonly IWindowsInstallerOwnedDirectoryCreationNative _native;

    internal WindowsInstallerOwnedDirectoryCreation(WindowsInstallerDirectoryCleanupLayout layout,
        IWindowsInstallerDirectoryLedgerPersistence ledger, IWindowsInstallerOwnedDirectoryCreationNative native)
    {
        _layout = layout;
        _ledger = ledger;
        _native = native;
    }

    internal static WindowsInstallerOwnedDirectoryCreation CreateDefault() => new(
        WindowsInstallerDirectoryCleanupLayout.CreateDefault(), WindowsInstallerDirectoryLedgerPersistence.CreateDefault(),
        WindowsInstallerOwnedDirectoryCreationNative.Instance);

    internal void Create(string path, DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (!_layout.TryGetRole(path, out InstallerDirectoryRole role))
        {
            // Existing Service/MihomoService and isolated testing roots retain their original behavior.
            _ = _native.CreateDirectory(path, security);
            return;
        }
        // Reject foreign ledger state before creating anything. This executes only in the elevated
        // helper's synchronous native directory operation, while it owns exclusive installer authority.
        WindowsInstallerDirectoryLedger? before = _ledger.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!_native.CreateDirectory(path, security)) { return; }
        using IWindowsInstallerCreatedDirectoryLease directory = _native.OpenCreatedDirectory(path);
        WindowsInstallerDirectoryLedger desired = (before ?? WindowsInstallerDirectoryLedger.Empty)
            .RecordCreated(role, directory.Identity);
        _ledger.SaveAsync(before, desired, CancellationToken.None).GetAwaiter().GetResult();
    }
}

internal sealed class WindowsInstallerOwnedDirectoryCreationNative : IWindowsInstallerOwnedDirectoryCreationNative
{
    internal static WindowsInstallerOwnedDirectoryCreationNative Instance { get; } = new();

    public bool CreateDirectory(string path, DirectorySecurity security)
    {
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        GCHandle pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject(), InheritHandle = 0,
            };
            if (CreateDirectoryNative(path, ref attributes)) { return true; }
            int error = Marshal.GetLastPInvokeError();
            if (error == 183) { return false; }
            throw new Win32Exception(error);
        }
        finally { pinned.Free(); }
    }

    public IWindowsInstallerCreatedDirectoryLease OpenCreatedDirectory(string path)
    {
        SafeFileHandle handle = CreateFile(path, 0x80000000U, 3, 0, 3, 0x02200000U, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            WindowsFileIdentity identity = WindowsFileSystemNative.GetOrdinaryDirectoryIdentity(handle);
            if (!WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(WindowsDirectoryReadLease.ReadSecuritySnapshot(handle)))
            {
                throw WindowsInstallerDirectoryLedger.Failure("created_directory_unsafe");
            }
            return new Lease(handle, identity);
        }
        catch { handle.Dispose(); throw; }
    }

    private sealed class Lease(SafeFileHandle handle, WindowsFileIdentity identity) : IWindowsInstallerCreatedDirectoryLease
    {
        public WindowsFileIdentity Identity => identity;
        public void Dispose() => handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal nint Descriptor;
        internal int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
