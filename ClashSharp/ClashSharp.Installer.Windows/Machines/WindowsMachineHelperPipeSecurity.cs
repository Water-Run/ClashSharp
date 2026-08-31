using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>Creates the single local pipe instance used by one elevated-helper transaction.</summary>
internal static class WindowsMachineHelperPipeSecurity
{
    private const int PipeBufferBytes = 4 * 1024 + sizeof(int);

    /// <summary>
    /// Creates a protected DACL for the exact logon, OTS administrators, and LocalSystem.
    /// </summary>
    internal static PipeSecurity Create(SecurityIdentifier logonSid)
    {
        ArgumentNullException.ThrowIfNull(logonSid);
        if (!logonSid.IsWellKnown(WellKnownSidType.LogonIdsSid))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.logon_identity_invalid");
        }

        SecurityIdentifier network = new(WellKnownSidType.NetworkSid, null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            network,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            logonSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            administrators,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            localSystem,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>Finds the exact logon SID shared by the unelevated and UAC-linked tokens.</summary>
    internal static SecurityIdentifier GetCurrentLogonSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The elevated-helper pipe is available only on Windows.");
        }

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            SecurityIdentifier logonSid = WindowsTokenLogonSidNative.Get(
                identity.AccessToken);
            if (!logonSid.IsWellKnown(WellKnownSidType.LogonIdsSid))
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.logon_identity_invalid");
            }

            return logonSid;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception
            or ArgumentException
            or UnauthorizedAccessException)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.logon_identity_invalid",
                exception);
        }
    }

    /// <summary>Creates the first and only server instance before the helper is launched.</summary>
    internal static NamedPipeServerStream CreateServerStream(
        InstallerMachineHelperBootstrap bootstrap,
        SecurityIdentifier logonSid)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        PipeSecurity security = Create(logonSid);
        return NamedPipeServerStreamAcl.Create(
            bootstrap.Invocation.BuildSessionPipeName(),
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            PipeBufferBytes,
            PipeBufferBytes,
            security,
            HandleInheritability.None,
            additionalAccessRights: 0);
    }
}

internal static class WindowsTokenLogonSidNative
{
    private const int TokenLogonSid = 28;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumTokenInformationBytes = 64 * 1024;

    internal static SecurityIdentifier Get(SafeAccessTokenHandle token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.IsClosed || token.IsInvalid)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.logon_identity_invalid");
        }

        _ = GetTokenInformation(
            token,
            TokenLogonSid,
            0,
            tokenInformationLength: 0,
            out int requiredLength);
        int firstError = Marshal.GetLastPInvokeError();
        if (firstError != ErrorInsufficientBuffer
            || requiredLength <= 0
            || requiredLength > MaximumTokenInformationBytes)
        {
            throw new Win32Exception(firstError);
        }

        nint buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            if (!GetTokenInformation(
                    token,
                    TokenLogonSid,
                    buffer,
                    requiredLength,
                    out int returnedLength))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (returnedLength != requiredLength)
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.logon_identity_invalid");
            }

            TokenGroups groups = Marshal.PtrToStructure<TokenGroups>(buffer);
            if (groups.GroupCount != 1 || groups.FirstGroup.Sid == 0)
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.logon_identity_invalid");
            }

            return new SecurityIdentifier(groups.FirstGroup.Sid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroups
    {
        internal uint GroupCount;
        internal SidAndAttributes FirstGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal nint Sid;
        internal uint Attributes;
    }
}
