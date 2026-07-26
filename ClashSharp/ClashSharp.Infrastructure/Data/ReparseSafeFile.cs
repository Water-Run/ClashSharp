using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSharp.ApplicationModel.Data;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Infrastructure.Data;

internal static class ReparseSafeFile
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const uint FileFlagOverlapped = 0x40000000;

    public static SafeFileHandle OpenRead(
        string path,
        FileShare share,
        bool asynchronous = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            return File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                share,
                FileOptions.RandomAccess
                    | (asynchronous ? FileOptions.Asynchronous : FileOptions.None));
        }

        SafeFileHandle handle = CreateFile(
            path,
            GenericRead,
            (uint)share,
            0,
            OpenExisting,
            FileAttributeNormal
                | FileFlagOpenReparsePoint
                | FileFlagRandomAccess
                | (asynchronous ? FileFlagOverlapped : 0),
            0);
        if (handle.IsInvalid)
        {
            int nativeError = Marshal.GetLastPInvokeError();
            handle.Dispose();
            Win32Exception nativeException = new(nativeError);
            if (nativeError is 2 or 3)
            {
                throw new FileNotFoundException(
                    $"The data-generation file '{path}' disappeared while it was opened.",
                    path,
                    nativeException);
            }

            throw new IOException(
                $"The data-generation file '{path}' could not be opened safely.",
                nativeException);
        }

        if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new DataGenerationStoreException(
                DataGenerationStoreError.UnsafePath,
                $"Data-generation file '{path}' is a reparse point.");
        }

        return handle;
    }

    public static bool IsTransientOpenFailure(Exception exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        return exception is IOException
            && exception.InnerException is Win32Exception nativeException
            && nativeException.NativeErrorCode is 2 or 3 or 32 or 33;
    }

#pragma warning disable SYSLIB1054 // SafeFileHandle source generation would require project-wide unsafe blocks.
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
#pragma warning restore SYSLIB1054
}
