using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Files;

internal static class WindowsFileSystemNative
{
    private const uint GenericRead = 0x8000_0000;
    private const uint Delete = 0x0001_0000;
    private const uint FileListDirectory = 0x0000_0001;
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x0000_0010;
    private const uint FileAttributeNormal = 0x0000_0080;
    private const uint FileAttributeReparsePoint = 0x0000_0400;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;

    internal static SafeFileHandle OpenOrdinaryFile(string path)
    {
        SafeFileHandle handle = CreateFile(
            path,
            GenericRead,
            FileShareRead,
            0,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            ByHandleFileInformation information = GetInformation(handle);
            if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
            {
                throw new IOException("The locked payload object is not an ordinary file.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenOrdinaryDirectory(string path)
    {
        return OpenOrdinaryDirectory(
            path,
            FileListDirectory | FileReadAttributes,
            FileShareRead);
    }

    internal static SafeFileHandle OpenOrdinaryDirectoryForMutationGuard(string path)
    {
        return OpenOrdinaryDirectory(
            path,
            FileListDirectory | FileReadAttributes,
            FileShareRead | FileShareWrite);
    }

    internal static SafeFileHandle OpenOrdinaryDirectoryForDeletion(string path)
    {
        return OpenOrdinaryDirectory(
            path,
            FileListDirectory | FileReadAttributes | Delete,
            FileShareRead | FileShareWrite);
    }

    private static SafeFileHandle OpenOrdinaryDirectory(
        string path,
        uint desiredAccess,
        uint shareMode)
    {
        SafeFileHandle handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            ByHandleFileInformation information = GetInformation(handle);
            if ((information.FileAttributes & FileAttributeDirectory) == 0
                || (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("The locked payload object is not an ordinary directory.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static WindowsFileIdentity GetOrdinaryFileIdentity(SafeFileHandle handle)
    {
        ByHandleFileInformation information = GetInformation(handle);
        if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            throw new IOException("The locked payload object changed kind.");
        }

        return Identity(information);
    }

    internal static WindowsFileIdentity GetOrdinaryDirectoryIdentity(SafeFileHandle handle)
    {
        ByHandleFileInformation information = GetInformation(handle);
        if ((information.FileAttributes & FileAttributeDirectory) == 0
            || (information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new IOException("The locked payload directory changed kind.");
        }

        return Identity(information);
    }

    private static WindowsFileIdentity Identity(ByHandleFileInformation information) =>
        new(
            information.VolumeSerialNumber,
            (ulong)information.FileIndexHigh << 32 | information.FileIndexLow);

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return information;
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

internal readonly record struct WindowsFileIdentity(uint VolumeSerialNumber, ulong FileIndex);
