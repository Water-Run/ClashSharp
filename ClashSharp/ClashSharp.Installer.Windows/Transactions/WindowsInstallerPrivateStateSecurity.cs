using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>Protects private evidence with exactly SYSTEM and Administrators full-control entries.</summary>
internal static class WindowsInstallerPrivateStateSecurity
{
    internal static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        Initialize(security, directory: true);
        return security;
    }

    internal static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        Initialize(security, directory: false);
        return security;
    }

    internal static void Validate(WindowsDirectorySecuritySnapshot security, bool directory)
    {
        AceFlags flags = directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
        if (!security.HasDacl || !security.DaclProtected
            || !string.Equals(security.OwnerSid, WindowsInstallerDirectorySecurityPolicy.AdministratorsSid, StringComparison.Ordinal)
            || security.AccessEntries.Count != 2
            || !HasExactEntry(security.AccessEntries, WindowsInstallerDirectorySecurityPolicy.LocalSystemSid, flags)
            || !HasExactEntry(security.AccessEntries, WindowsInstallerDirectorySecurityPolicy.AdministratorsSid, flags))
        {
            throw new InstallerProtocolException("installer.owner_transfer.private_acl_invalid");
        }
    }

    private static void Initialize(FileSystemSecurity security, bool directory)
    {
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WindowsInstallerDirectorySecurityPolicy.AdministratorsSid));
        InheritanceFlags inheritance = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        foreach (string sid in new[]
        {
            WindowsInstallerDirectorySecurityPolicy.LocalSystemSid,
            WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
    }

    private static bool HasExactEntry(IReadOnlyList<WindowsDirectoryAce> entries, string sid, AceFlags flags)
    {
        WindowsDirectoryAce[] matching = entries.Where(entry => string.Equals(entry.Sid, sid, StringComparison.Ordinal)).ToArray();
        return matching.Length == 1
            && matching[0].Kind == WindowsDirectoryAceKind.Allow
            && matching[0].AccessMask == (int)FileSystemRights.FullControl
            && matching[0].Flags == flags
            && !matching[0].IsObjectSpecific;
    }
}
