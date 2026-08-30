using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;

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

        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier[] logonSids = identity.Groups?
            .OfType<SecurityIdentifier>()
            .Where(static sid => sid.IsWellKnown(WellKnownSidType.LogonIdsSid))
            .ToArray()
            ?? [];
        if (logonSids.Length != 1)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.logon_identity_invalid");
        }

        return logonSids[0];
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
