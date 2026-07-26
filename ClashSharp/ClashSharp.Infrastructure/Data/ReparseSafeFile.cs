using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSharp.ApplicationModel.Data;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Infrastructure.Data;

internal static class ReparseSafeFile
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagWriteThrough = 0x80000000;

    public static SafeFileHandle OpenRead(
        string path,
        FileShare share,
        bool asynchronous = false) =>
        Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            share,
            FileOptions.RandomAccess
                | (asynchronous
                    ? FileOptions.Asynchronous
                    : FileOptions.None),
            requireExactPath: false);

    public static SafeFileHandle OpenWriteLock(string path) =>
        Open(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            FileOptions.RandomAccess,
            requireExactPath: true);

    public static SafeFileHandle CreateWrite(
        string path,
        bool asynchronous = false) =>
        Open(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.WriteThrough
                | (asynchronous
                    ? FileOptions.Asynchronous
                    : FileOptions.None),
            requireExactPath: true);

    public static SafeFileHandle OpenReadWrite(
        string path,
        FileShare share) =>
        Open(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            share,
            FileOptions.WriteThrough | FileOptions.RandomAccess,
            requireExactPath: true);

    private static SafeFileHandle Open(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        bool requireExactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            SafeFileHandle portableHandle = File.OpenHandle(
                path,
                mode,
                access,
                share,
                options);
            ValidateOpenedHandle(portableHandle, path, requireExactPath);
            return portableHandle;
        }

        uint creationDisposition = mode switch
        {
            FileMode.CreateNew => CreateNew,
            FileMode.Open => OpenExisting,
            FileMode.OpenOrCreate => OpenAlways,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
        uint desiredAccess = access switch
        {
            FileAccess.Read => GenericRead,
            FileAccess.Write => GenericWrite,
            FileAccess.ReadWrite => GenericRead | GenericWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, null),
        };
        uint flags = FileAttributeNormal | FileFlagOpenReparsePoint;
        if ((options & FileOptions.RandomAccess) != 0)
        {
            flags |= FileFlagRandomAccess;
        }

        if ((options & FileOptions.Asynchronous) != 0)
        {
            flags |= FileFlagOverlapped;
        }

        if ((options & FileOptions.WriteThrough) != 0)
        {
            flags |= FileFlagWriteThrough;
        }

        SafeFileHandle handle = CreateFile(
            path,
            desiredAccess,
            (uint)share,
            0,
            creationDisposition,
            flags,
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

        try
        {
            ValidateOpenedHandle(handle, path, requireExactPath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
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

    private static void ValidateOpenedHandle(
        SafeFileHandle handle,
        string expectedPath,
        bool requireExactPath)
    {
        if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
        {
            throw CreateUnsafePathException(
                $"Data-generation file '{expectedPath}' is a reparse point.");
        }

        if (!OperatingSystem.IsWindows() || !requireExactPath)
        {
            return;
        }

        string actualPath = GetFinalPath(handle);
        string normalizedExpected = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(expectedPath));
        if (!string.Equals(
                actualPath,
                normalizedExpected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw CreateUnsafePathException(
                $"Data-generation file '{expectedPath}' resolved outside its expected path.");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        while (true)
        {
            uint length = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Length,
                fileNameNormalized: 0);
            if (length == 0)
            {
                throw new IOException(
                    "The opened data-generation file path could not be resolved.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            if (length < buffer.Length)
            {
                return NormalizeWindowsDevicePath(
                    new string(buffer, startIndex: 0, checked((int)length)));
            }

            Array.Resize(ref buffer, checked((int)length + 1));
        }
    }

    private static string NormalizeWindowsDevicePath(string path)
    {
        const string deviceUncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        string normalized = path.StartsWith(
            deviceUncPrefix,
            StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[deviceUncPrefix.Length..]
            : path.StartsWith(
                devicePrefix,
                StringComparison.OrdinalIgnoreCase)
                ? path[devicePrefix.Length..]
                : path;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized));
    }

    private static DataGenerationStoreException CreateUnsafePathException(
        string message) =>
        new(DataGenerationStoreError.UnsafePath, message);

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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint fileNameNormalized);
#pragma warning restore SYSLIB1054
}
