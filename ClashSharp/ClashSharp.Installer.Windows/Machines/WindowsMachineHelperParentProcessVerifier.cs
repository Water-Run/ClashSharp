using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperParentProcessLease : IDisposable
{
    int ProcessId { get; }

    string UserSid { get; }

    void VerifyAlive();
}

internal interface IWindowsMachineHelperParentProcessVerifier
{
    IWindowsMachineHelperParentProcessLease Acquire(
        int expectedParentProcessId,
        string expectedExecutablePath);
}

/// <summary>
/// Pins the bootstrap parent process object, verifies its executable is the same signed Installer,
/// and prevents PID reuse for the complete helper session.
/// </summary>
internal sealed class WindowsMachineHelperParentProcessVerifier
    : IWindowsMachineHelperParentProcessVerifier
{
    private readonly IWindowsMachineHelperParentProcessNative _native;

    internal WindowsMachineHelperParentProcessVerifier()
        : this(WindowsMachineHelperParentProcessNative.Instance)
    {
    }

    internal WindowsMachineHelperParentProcessVerifier(
        IWindowsMachineHelperParentProcessNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public IWindowsMachineHelperParentProcessLease Acquire(
        int expectedParentProcessId,
        string expectedExecutablePath)
    {
        if (expectedParentProcessId <= 0)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.parent_process_invalid");
        }

        string expectedPath = ValidateExecutablePath(expectedExecutablePath);
        SafeProcessHandle handle;
        try
        {
            handle = _native.Open(expectedParentProcessId);
        }
        catch (Exception exception) when (IsRecoverableNativeFailure(exception))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.parent_process_open_failed",
                exception);
        }

        try
        {
            string observedPath = Path.GetFullPath(_native.QueryImagePath(handle));
            if (!string.Equals(observedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.parent_image_mismatch");
            }

            string userSid = _native.QueryUserSid(handle);
            InstallerProtocolValidation.ValidateTargetSid(userSid);
            var lease = new WindowsMachineHelperParentProcessLease(
                expectedParentProcessId,
                userSid,
                handle,
                _native);
            lease.VerifyAlive();
            handle = null!;
            return lease;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableNativeFailure(exception))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.parent_process_query_failed",
                exception);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new InstallerProtocolException(
                "installer.elevation.executable_path_invalid");
        }

        return Path.GetFullPath(executablePath);
    }

    private static bool IsRecoverableNativeFailure(Exception exception) =>
        exception is Win32Exception
            or IOException
            or UnauthorizedAccessException;
}

internal sealed class WindowsMachineHelperParentProcessLease
    : IWindowsMachineHelperParentProcessLease
{
    private readonly IWindowsMachineHelperParentProcessNative _native;
    private SafeProcessHandle? _handle;

    internal WindowsMachineHelperParentProcessLease(
        int processId,
        string userSid,
        SafeProcessHandle handle,
        IWindowsMachineHelperParentProcessNative native)
    {
        InstallerProtocolValidation.ValidateTargetSid(userSid);
        ProcessId = processId;
        UserSid = userSid;
        _handle = handle;
        _native = native;
    }

    public int ProcessId { get; }

    public string UserSid { get; }

    public void VerifyAlive()
    {
        SafeProcessHandle handle = _handle
            ?? throw new ObjectDisposedException(GetType().FullName);
        if (!_native.IsAlive(handle))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.parent_process_exited");
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

internal interface IWindowsMachineHelperParentProcessNative
{
    SafeProcessHandle Open(int processId);

    string QueryImagePath(SafeProcessHandle process);

    string QueryUserSid(SafeProcessHandle process);

    bool IsAlive(SafeProcessHandle process);
}

internal sealed class WindowsMachineHelperParentProcessNative
    : IWindowsMachineHelperParentProcessNative
{
    private const uint Synchronize = 0x0010_0000;
    private const uint QueryLimitedInformation = 0x0000_1000;
    private const uint TokenQuery = 0x0000_0008;
    private const uint StillActive = 259;
    private const int TokenUserInformationClass = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumPathCharacters = 32_767;
    private const uint MaximumTokenInformationBytes = 64 * 1024;

    internal static WindowsMachineHelperParentProcessNative Instance { get; } = new();

    private WindowsMachineHelperParentProcessNative()
    {
    }

    public SafeProcessHandle Open(int processId)
    {
        SafeProcessHandle handle = OpenProcess(
            Synchronize | QueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    public string QueryImagePath(SafeProcessHandle process)
    {
        char[] path = GC.AllocateUninitializedArray<char>(MaximumPathCharacters);
        int length = path.Length;
        if (!QueryFullProcessImageName(process, flags: 0, path, ref length)
            || length <= 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new string(path, 0, length);
    }

    public string QueryUserSid(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using (token)
        {
            bool unexpectedSuccess = GetTokenInformation(
                token,
                TokenUserInformationClass,
                tokenInformation: 0,
                tokenInformationLength: 0,
                out uint requiredBytes);
            int sizingError = Marshal.GetLastPInvokeError();
            if (unexpectedSuccess
                || sizingError != ErrorInsufficientBuffer
                || requiredBytes is 0 or > MaximumTokenInformationBytes)
            {
                throw new InvalidDataException(
                    "The parent process token returned an invalid user buffer size.");
            }

            nint buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                if (!GetTokenInformation(
                        token,
                        TokenUserInformationClass,
                        buffer,
                        requiredBytes,
                        out uint returnedBytes))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                if (returnedBytes is 0 or > MaximumTokenInformationBytes
                    || returnedBytes > requiredBytes)
                {
                    throw new InvalidDataException(
                        "The parent process token returned an invalid user buffer.");
                }

                TokenUser tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
                if (tokenUser.User.Sid == 0)
                {
                    throw new InvalidDataException(
                        "The parent process token omitted its user SID.");
                }

                return new SecurityIdentifier(tokenUser.User.Sid).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public bool IsAlive(SafeProcessHandle process)
    {
        if (!GetExitCodeProcess(process, out uint exitCode))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return exitCode == StillActive;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(
        SafeProcessHandle process,
        out uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        internal SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal nint Sid;
        internal uint Attributes;
    }
}
