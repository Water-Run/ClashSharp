using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Windows.FileSecurity;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal sealed record WindowsOwnerTransferAccessObservation(
    bool IsDirectory, bool IsReparsePoint, uint LinkCount, WindowsDirectorySecuritySnapshot Security);

internal sealed record WindowsOwnerTransferAccessEntry(string Path, bool IsDirectory);

internal interface IWindowsOwnerTransferAccessLease : IDisposable
{
    WindowsOwnerTransferAccessObservation Observe();

    byte[] ReadFileBytes(int maximumBytes);

    void ApplyOwnerAccess(string previousSid, string nextSid, bool inherited);
}

internal interface IWindowsOwnerTransferAccessNative
{
    IWindowsOwnerTransferAccessLease Open(string path, bool directory, bool canChangeAccess);

    IEnumerable<WindowsOwnerTransferAccessEntry> EnumerateChildren(string path);
}

/// <summary>
/// Changes only a held object's DACL. It has no create, delete, owner-change or unbounded data-write
/// operation. The caller must first pin and validate the whole inheritance scope; SetSecurityInfo
/// can propagate inheritable entries to existing unprotected children.
/// </summary>
internal sealed class WindowsOwnerTransferAccessNative : IWindowsOwnerTransferAccessNative
{
    public IWindowsOwnerTransferAccessLease Open(string path, bool directory, bool canChangeAccess) =>
        Lease.Open(path, directory, canChangeAccess);

    public IEnumerable<WindowsOwnerTransferAccessEntry> EnumerateChildren(string path)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            yield return new WindowsOwnerTransferAccessEntry(
                entry, (File.GetAttributes(entry) & FileAttributes.Directory) != 0);
        }
    }

    private sealed class Lease : IWindowsOwnerTransferAccessLease
    {
        private const uint ReadControl = 0x0002_0000;
        private const uint WriteDacl = 0x0004_0000;
        private const uint ReadDataOrListDirectory = 0x0000_0001;
        private const uint ReadAttributes = 0x0000_0080;
        private const uint DaclSecurityInformation = 0x0000_0004;
        private const uint ProtectedDaclSecurityInformation = 0x8000_0000;
        private const uint UnprotectedDaclSecurityInformation = 0x2000_0000;
        private readonly SafeFileHandle _handle;
        private readonly bool _directory;
        private readonly bool _canChangeAccess;

        private Lease(SafeFileHandle handle, bool directory, bool canChangeAccess)
        {
            _handle = handle;
            _directory = directory;
            _canChangeAccess = canChangeAccess;
        }

        internal static Lease Open(string path, bool directory, bool canChangeAccess)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            SafeFileHandle handle = CreateFile(path,
                ReadControl | ReadDataOrListDirectory | ReadAttributes | (canChangeAccess ? WriteDacl : 0),
                directory ? 3u : 1u, 0, 3, 0x0200_0000 | 0x0020_0000, 0);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            var lease = new Lease(handle, directory, canChangeAccess);
            try
            {
                _ = lease.Observe();
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public WindowsOwnerTransferAccessObservation Observe()
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (!GetFileInformationByHandle(_handle, out FileInformation information))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            bool directory = (information.Attributes & 0x10) != 0;
            bool reparse = (information.Attributes & 0x400) != 0;
            if (directory != _directory || reparse || (!directory && information.LinkCount != 1))
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_object_invalid");
            }
            return new WindowsOwnerTransferAccessObservation(
                directory, reparse, information.LinkCount, WindowsDirectoryReadLease.ReadSecuritySnapshot(_handle));
        }

        public byte[] ReadFileBytes(int maximumBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, 16 * 1024);
            _ = Observe();
            if (_directory)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_object_invalid");
            }
            long length = RandomAccess.GetLength(_handle);
            if (length is < 1 || length > maximumBytes)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_file_invalid");
            }
            byte[] bytes = new byte[checked((int)length)];
            try
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = RandomAccess.Read(_handle, bytes.AsSpan(offset), offset);
                    if (read == 0)
                    {
                        throw new InstallerProtocolException("installer.owner_transfer.access_file_invalid");
                    }
                    offset += read;
                }
                return bytes;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw;
            }
        }

        public void ApplyOwnerAccess(string previousSid, string nextSid, bool inherited)
        {
            WindowsOwnerTransferAccessPolicy.ValidateParticipants(previousSid, nextSid);
            if (!_canChangeAccess)
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_read_only");
            }
            WindowsDirectorySecuritySnapshot before = Observe().Security;
            if (WindowsOwnerTransferAccessPolicy.HasOwnerAccess(before, nextSid, _directory, inherited))
            {
                return;
            }
            if (!WindowsOwnerTransferAccessPolicy.HasOwnerAccess(before, previousSid, _directory, inherited))
            {
                throw new InstallerProtocolException("installer.owner_transfer.access_acl_invalid");
            }

            byte[] dacl = WindowsOwnerTransferAccessPolicy.CreateDacl(nextSid, _directory, inherited);
            try
            {
                uint error = SetSecurityInfo(_handle, 1,
                    DaclSecurityInformation | (inherited ? UnprotectedDaclSecurityInformation : ProtectedDaclSecurityInformation),
                    0, 0, dacl, 0);
                if (error != 0)
                {
                    throw new Win32Exception(checked((int)error));
                }
                if (!WindowsOwnerTransferAccessPolicy.HasOwnerAccess(Observe().Security, nextSid, _directory, inherited))
                {
                    throw new InstallerProtocolException("installer.owner_transfer.access_postcondition_failed");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dacl);
            }
        }

        public void Dispose() => _handle.Dispose();

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
            nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        [DllImport("advapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType,
            uint securityInformation, nint owner, nint group, [In] byte[] dacl, nint sacl);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint LinkCount;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }
    }
}
