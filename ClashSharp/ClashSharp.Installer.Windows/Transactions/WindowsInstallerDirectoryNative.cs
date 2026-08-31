using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Transactions;

internal sealed class WindowsInstallerDirectoryNative : IWindowsInstallerDirectoryNative
{
    public void CreateDirectory(string path, DirectorySecurity security)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(security);
        new DirectoryInfo(path).Create(security);
    }

    /// <inheritdoc />
    public IWindowsInstallerDirectoryLease OpenDirectory(string path, bool preventRename) =>
        new WindowsInstallerDirectoryLease(path, preventRename);
}

internal sealed class WindowsInstallerDirectoryLease : IWindowsInstallerDirectoryLease
{
    private const uint ReadControl = 0x0002_0000;
    private const uint Delete = 0x0001_0000;
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x0000_0010;
    private const uint FileAttributeReparsePoint = 0x0000_0400;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint OwnerSecurityInformation = 0x0000_0001;
    private const uint DaclSecurityInformation = 0x0000_0004;
    private const int SeFileObject = 1;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    internal WindowsInstallerDirectoryLease(string path, bool preventRename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _handle = CreateFile(
            path,
            ReadControl | FileReadAttributes | (preventRename ? Delete : 0),
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    public WindowsInstallerDirectoryObservation Observe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GetFileInformationByHandle(_handle, out ByHandleFileInformation information))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        WindowsInstallerDirectorySecuritySnapshot security = ReadSecuritySnapshot(_handle);
        return new WindowsInstallerDirectoryObservation(
            IsDirectory: (information.FileAttributes & FileAttributeDirectory) != 0,
            IsReparsePoint: (information.FileAttributes & FileAttributeReparsePoint) != 0,
            security);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _handle.Dispose();
        _disposed = true;
    }

    private static WindowsInstallerDirectorySecuritySnapshot ReadSecuritySnapshot(
        SafeFileHandle handle)
    {
        uint error = GetSecurityInfo(
            handle,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out nint securityDescriptor);
        if (error != 0)
        {
            throw new Win32Exception(checked((int)error));
        }

        if (securityDescriptor == 0)
        {
            throw new InvalidDataException(
                "Windows returned no directory security descriptor.");
        }

        try
        {
            uint descriptorLength = GetSecurityDescriptorLength(securityDescriptor);
            if (descriptorLength is 0 or > 65_536)
            {
                throw new InvalidDataException(
                    "The directory security descriptor length is invalid.");
            }

            byte[] bytes = new byte[checked((int)descriptorLength)];
            Marshal.Copy(securityDescriptor, bytes, 0, bytes.Length);
            var raw = new RawSecurityDescriptor(bytes, 0);
            var entries = new List<WindowsInstallerDirectoryAce>();
            RawAcl? dacl = raw.DiscretionaryAcl;
            if (dacl is not null)
            {
                for (int index = 0; index < dacl.Count; index++)
                {
                    entries.Add(ToObservation(dacl[index]));
                }
            }

            return new WindowsInstallerDirectorySecuritySnapshot(
                raw.Owner?.Value,
                HasDacl: dacl is not null,
                DaclProtected: (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                entries);
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    private static WindowsInstallerDirectoryAce ToObservation(GenericAce genericAce)
    {
        if (genericAce is not QualifiedAce qualifiedAce
            || qualifiedAce.IsCallback)
        {
            return new WindowsInstallerDirectoryAce(
                string.Empty,
                WindowsInstallerDirectoryAceKind.Unsupported,
                0,
                AceFlags.None,
                IsObjectSpecific: genericAce is ObjectAce);
        }

        WindowsInstallerDirectoryAceKind kind = qualifiedAce.AceQualifier switch
        {
            AceQualifier.AccessAllowed => WindowsInstallerDirectoryAceKind.Allow,
            AceQualifier.AccessDenied => WindowsInstallerDirectoryAceKind.Deny,
            _ => WindowsInstallerDirectoryAceKind.Unsupported,
        };
        return new WindowsInstallerDirectoryAce(
            qualifiedAce.SecurityIdentifier.Value,
            kind,
            qualifiedAce.AccessMask,
            qualifiedAce.AceFlags,
            IsObjectSpecific: qualifiedAce is ObjectAce);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint securityDescriptor);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSecurityDescriptorLength(nint securityDescriptor);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }
}
