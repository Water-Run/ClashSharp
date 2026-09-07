using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Recognizes the complete existing or transferred ACL; inheritance and private boundaries are
/// part of the contract. Matching an owner SID alone never authorizes replacement of a DACL.
/// </summary>
internal static class WindowsOwnerTransferAccessPolicy
{
    internal static void ValidateParticipants(string previousSid, string nextSid)
    {
        ValidateAccountSid(previousSid);
        ValidateAccountSid(nextSid);
        if (string.Equals(previousSid, nextSid, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.owner_transfer.access_identity_invalid");
        }
    }

    internal static bool HasOwnerAccess(
        WindowsDirectorySecuritySnapshot security, string sid, bool directory, bool inherited) =>
        security.HasDacl && security.DaclProtected == !inherited
        && security.OwnerSid == WindowsDirectoryAccessPolicy.AdministratorsSid
        && security.AccessEntries.Count == 3
        && HasRule(security, WindowsDirectoryAccessPolicy.LocalSystemSid, FileSystemRights.FullControl, Flags(directory, inherited))
        && HasRule(security, WindowsDirectoryAccessPolicy.AdministratorsSid, FileSystemRights.FullControl, Flags(directory, inherited))
        && HasRule(security, sid, WindowsDirectoryAccessPolicy.OwnerReadOnlyRights, Flags(directory, inherited));

    internal static bool HasPrivateDirectoryAccess(WindowsDirectorySecuritySnapshot security, bool service) =>
        security.HasDacl && security.DaclProtected && security.AccessEntries.Count == 2
        && security.OwnerSid == (service ? WindowsDirectoryAccessPolicy.LocalSystemSid : WindowsDirectoryAccessPolicy.AdministratorsSid)
        && HasRule(security, WindowsDirectoryAccessPolicy.LocalSystemSid, FileSystemRights.FullControl, Flags(directory: true, inherited: false))
        && HasRule(security, WindowsDirectoryAccessPolicy.AdministratorsSid, FileSystemRights.FullControl, Flags(directory: true, inherited: false));

    internal static byte[] CreateDacl(string targetSid, bool directory, bool inherited)
    {
        ValidateAccountSid(targetSid);
        var acl = new RawAcl(GenericAcl.AclRevision, 3);
        foreach ((string sid, FileSystemRights rights) in new[]
        {
            (WindowsDirectoryAccessPolicy.LocalSystemSid, FileSystemRights.FullControl),
            (WindowsDirectoryAccessPolicy.AdministratorsSid, FileSystemRights.FullControl),
            (targetSid, WindowsDirectoryAccessPolicy.OwnerReadOnlyRights),
        })
        {
            acl.InsertAce(acl.Count, new CommonAce(Flags(directory, inherited),
                AceQualifier.AccessAllowed, (int)rights, new SecurityIdentifier(sid), isCallback: false, opaque: null));
        }
        byte[] bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static AceFlags Flags(bool directory, bool inherited) =>
        (directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None)
        | (inherited ? AceFlags.Inherited : AceFlags.None);

    private static bool HasRule(WindowsDirectorySecuritySnapshot security, string sid, FileSystemRights rights, AceFlags flags)
    {
        WindowsDirectoryAce[] entries = security.AccessEntries.Where(entry => entry.Sid == sid).ToArray();
        return entries.Length == 1 && entries[0].Kind == WindowsDirectoryAceKind.Allow
            && entries[0].AccessMask == (int)rights && entries[0].Flags == flags && !entries[0].IsObjectSpecific;
    }

    private static void ValidateAccountSid(string sid)
    {
        try
        {
            var account = new SecurityIdentifier(sid);
            if (account.IsAccountSid() && string.Equals(account.Value, sid, StringComparison.Ordinal))
            {
                return;
            }
        }
        catch (ArgumentException)
        {
            // Return only the stable protocol code; account parser exceptions may contain the SID.
        }
        throw new InstallerProtocolException("installer.owner_transfer.access_identity_invalid");
    }
}
