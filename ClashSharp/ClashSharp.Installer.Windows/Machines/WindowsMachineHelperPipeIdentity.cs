using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>Abstracts exact named-pipe peer PID queries for deterministic policy tests.</summary>
internal interface IWindowsMachineHelperPipeIdentityNative
{
    uint GetClientProcessId(SafePipeHandle connectedServerPipe);

    uint GetServerProcessId(SafePipeHandle connectedClientPipe);
}


/// <summary>Binds each connected pipe endpoint to the process selected before connection.</summary>
internal sealed class WindowsMachineHelperPipeIdentity
{
    private readonly IWindowsMachineHelperPipeIdentityNative _native;

    internal WindowsMachineHelperPipeIdentity()
        : this(WindowsMachineHelperPipeIdentityNative.Instance)
    {
    }

    internal WindowsMachineHelperPipeIdentity(
        IWindowsMachineHelperPipeIdentityNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    /// <summary>Verifies that the client of a parent-owned pipe is the launched helper PID.</summary>
    internal void VerifyClient(
        SafePipeHandle connectedServerPipe,
        int expectedHelperProcessId) =>
        Verify(
            connectedServerPipe,
            expectedHelperProcessId,
            _native.GetClientProcessId);

    /// <summary>Verifies that the server of a helper-owned handle is the expected parent PID.</summary>
    internal void VerifyServer(
        SafePipeHandle connectedClientPipe,
        int expectedParentProcessId) =>
        Verify(
            connectedClientPipe,
            expectedParentProcessId,
            _native.GetServerProcessId);

    private static void Verify(
        SafePipeHandle connectedPipe,
        int expectedProcessId,
        Func<SafePipeHandle, uint> query)
    {
        ArgumentNullException.ThrowIfNull(connectedPipe);
        if (connectedPipe.IsClosed || connectedPipe.IsInvalid || expectedProcessId <= 0)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.pipe_peer_identity_invalid");
        }

        try
        {
            uint observedProcessId = query(connectedPipe);
            if (observedProcessId == 0
                || observedProcessId != checked((uint)expectedProcessId))
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.pipe_peer_identity_invalid");
            }
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.pipe_peer_query_failed",
                exception);
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

/// <summary>Uses kernel32 to query the process bound to an already-connected pipe instance.</summary>
internal sealed class WindowsMachineHelperPipeIdentityNative
    : IWindowsMachineHelperPipeIdentityNative
{
    internal static WindowsMachineHelperPipeIdentityNative Instance { get; } = new();

    private WindowsMachineHelperPipeIdentityNative()
    {
    }

    public uint GetClientProcessId(SafePipeHandle connectedServerPipe)
    {
        if (!GetNamedPipeClientProcessId(connectedServerPipe, out uint processId))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return processId;
    }

    public uint GetServerProcessId(SafePipeHandle connectedClientPipe)
    {
        if (!GetNamedPipeServerProcessId(connectedClientPipe, out uint processId))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return processId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}
