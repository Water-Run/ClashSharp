using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Service;

/// <summary>Verifies that a connected pipe is owned by the SCM-managed mihomo service process.</summary>
internal interface IMihomoServicePipeServerIdentityVerifier
{
    /// <summary>Rejects a connected pipe unless its server is the expected Windows service process.</summary>
    void Verify(SafePipeHandle connectedPipeHandle);
}

/// <summary>Contains the SCM-reported process identity fields required by the pipe verifier.</summary>
internal readonly record struct MihomoWindowsServiceProcessStatus(
    uint ServiceType,
    uint CurrentState,
    uint ProcessId);

/// <summary>Abstracts the two native identity queries so policy can be tested without a real service.</summary>
internal interface IMihomoServiceIdentityNativeApi
{
    /// <summary>Gets the process identifier of the server for one connected pipe instance.</summary>
    uint GetNamedPipeServerProcessId(SafePipeHandle connectedPipeHandle);

    /// <summary>Gets the SCM process status for the exact service name.</summary>
    MihomoWindowsServiceProcessStatus QueryServiceProcessStatus(string serviceName);
}

/// <summary>Authenticates a pipe server by binding it to an exact running own-process SCM service.</summary>
internal sealed class WindowsMihomoServicePipeServerIdentityVerifier
    : IMihomoServicePipeServerIdentityVerifier
{
    internal const uint OwnProcessServiceType = 0x00000010;
    internal const uint Win32ServiceTypeMask = 0x00000030;
    internal const uint RunningServiceState = 0x00000004;

    private readonly string _serviceName;
    private readonly IMihomoServiceIdentityNativeApi _nativeApi;

    /// <summary>Initializes the production verifier for one fixed Windows service.</summary>
    internal WindowsMihomoServicePipeServerIdentityVerifier(string serviceName)
        : this(serviceName, WindowsMihomoServiceIdentityNativeApi.Instance)
    {
    }

    /// <summary>Initializes a verifier with an injectable native seam.</summary>
    internal WindowsMihomoServicePipeServerIdentityVerifier(
        string serviceName,
        IMihomoServiceIdentityNativeApi nativeApi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _serviceName = serviceName;
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
    }

    /// <inheritdoc />
    public void Verify(SafePipeHandle connectedPipeHandle)
    {
        ArgumentNullException.ThrowIfNull(connectedPipeHandle);
        if (connectedPipeHandle.IsClosed || connectedPipeHandle.IsInvalid)
        {
            throw new ArgumentException(
                "The connected pipe handle must be open and valid.",
                nameof(connectedPipeHandle));
        }

        uint pipeServerProcessId = _nativeApi.GetNamedPipeServerProcessId(
            connectedPipeHandle);
        MihomoWindowsServiceProcessStatus service = _nativeApi
            .QueryServiceProcessStatus(_serviceName);
        if ((service.ServiceType & Win32ServiceTypeMask) != OwnProcessServiceType
            || service.CurrentState != RunningServiceState
            || service.ProcessId == 0
            || pipeServerProcessId == 0
            || service.ProcessId != pipeServerProcessId)
        {
            throw new MihomoServicePipeServerIdentityException();
        }
    }
}

/// <summary>Signals that a connected pipe is not owned by the expected SCM service process.</summary>
internal sealed class MihomoServicePipeServerIdentityException : UnauthorizedAccessException
{
    internal MihomoServicePipeServerIdentityException()
        : base("The mihomo service pipe server identity could not be authenticated.")
    {
    }
}

/// <summary>Uses direct Win32 pipe and SCM APIs to obtain service process identity.</summary>
internal sealed class WindowsMihomoServiceIdentityNativeApi : IMihomoServiceIdentityNativeApi
{
    private const uint ScManagerConnect = 0x00000001;
    private const uint ServiceQueryStatus = 0x00000004;
    private const int ScStatusProcessInfo = 0;

    internal static WindowsMihomoServiceIdentityNativeApi Instance { get; } = new();

    private WindowsMihomoServiceIdentityNativeApi()
    {
    }

    /// <inheritdoc />
    public uint GetNamedPipeServerProcessId(SafePipeHandle connectedPipeHandle)
    {
        ArgumentNullException.ThrowIfNull(connectedPipeHandle);
        if (!GetNamedPipeServerProcessIdNative(
                connectedPipeHandle,
                out uint serverProcessId))
        {
            throw CreateNativeIOException(
                "The mihomo service pipe server process could not be queried.");
        }

        return serverProcessId;
    }

    /// <inheritdoc />
    public MihomoWindowsServiceProcessStatus QueryServiceProcessStatus(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        nint serviceControlManager = OpenSCManagerNative(
            machineName: null,
            databaseName: null,
            ScManagerConnect);
        if (serviceControlManager == 0)
        {
            throw CreateNativeIOException(
                "The Windows service control manager could not be opened.");
        }

        try
        {
            nint service = OpenServiceNative(
                serviceControlManager,
                serviceName,
                ServiceQueryStatus);
            if (service == 0)
            {
                throw CreateNativeIOException(
                    "The mihomo Windows service could not be opened for a status query.");
            }

            try
            {
                uint statusSize = (uint)Marshal.SizeOf<ServiceStatusProcess>();
                if (!QueryServiceStatusExNative(
                        service,
                        ScStatusProcessInfo,
                        out ServiceStatusProcess status,
                        statusSize,
                        out _))
                {
                    throw CreateNativeIOException(
                        "The mihomo Windows service process status could not be queried.");
                }

                return new MihomoWindowsServiceProcessStatus(
                    status.ServiceType,
                    status.CurrentState,
                    status.ProcessId);
            }
            finally
            {
                _ = CloseServiceHandleNative(service);
            }
        }
        finally
        {
            _ = CloseServiceHandleNative(serviceControlManager);
        }
    }

    private static IOException CreateNativeIOException(string message)
    {
        int error = Marshal.GetLastWin32Error();
        return new IOException(message, new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessIdNative(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenSCManagerNative(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenServiceNative(
        nint serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusExNative(
        nint service,
        int informationLevel,
        out ServiceStatusProcess status,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandleNative(nint serviceHandle);
}
