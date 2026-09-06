using System.Security.AccessControl;
using System.Security.Principal;

namespace ClashSharp.Windows.FileSecurity;

/// <summary>
/// Defines the owner-readable machine directory contract shared by Installer and service.
/// This source is compiled into each host without introducing an execution dependency.
/// </summary>
internal static class WindowsDirectoryAccessPolicy
{
    internal const string LocalSystemSid = "S-1-5-18";
    internal const string AdministratorsSid = "S-1-5-32-544";
    internal const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    internal const FileSystemRights OwnerReadOnlyRights =
        FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
    private const AceFlags DirectoryInheritance = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
    private const int GenericAll = 0x1000_0000;
    private const FileSystemRights DangerousAnchorRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    internal static DirectorySecurity CreateOwnerReadableDirectorySecurity(string targetSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSid);
        var target = new SecurityIdentifier(targetSid);
        if (!target.IsAccountSid() || !string.Equals(target.Value, targetSid, StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical account SID is required.", nameof(targetSid));
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(AdministratorsSid));
        foreach ((string sid, FileSystemRights rights) in new[]
        {
            (LocalSystemSid, FileSystemRights.FullControl),
            (AdministratorsSid, FileSystemRights.FullControl),
            (targetSid, OwnerReadOnlyRights),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid), rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    internal static bool HasExactOwnerReadOnlyAccess(WindowsDirectorySecuritySnapshot security, string targetSid) =>
        security.HasDacl && security.DaclProtected
        && string.Equals(security.OwnerSid, AdministratorsSid, StringComparison.Ordinal)
        && security.AccessEntries.Count == 3
        && HasExactRule(security.AccessEntries, LocalSystemSid, FileSystemRights.FullControl)
        && HasExactRule(security.AccessEntries, AdministratorsSid, FileSystemRights.FullControl)
        && HasExactRule(security.AccessEntries, targetSid, OwnerReadOnlyRights);

    internal static bool IsTrustedRenameAnchor(WindowsDirectorySecuritySnapshot security) =>
        security.HasDacl && IsTrustedAuthority(security.OwnerSid)
        && security.AccessEntries.All(entry =>
            entry.Kind != WindowsDirectoryAceKind.Unsupported
            && !(entry.Kind == WindowsDirectoryAceKind.Allow
                && (entry.Flags & AceFlags.InheritOnly) == 0
                && !IsTrustedAuthority(entry.Sid)
                && (entry.AccessMask & ((int)DangerousAnchorRights | GenericAll)) != 0));

    private static bool HasExactRule(IReadOnlyList<WindowsDirectoryAce> entries, string sid, FileSystemRights rights)
    {
        WindowsDirectoryAce[] matches = entries.Where(entry => string.Equals(entry.Sid, sid, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            && matches[0].Kind == WindowsDirectoryAceKind.Allow
            && matches[0].AccessMask == (int)rights
            && matches[0].Flags == DirectoryInheritance
            && !matches[0].IsObjectSpecific;
    }

    private static bool IsTrustedAuthority(string? sid) =>
        sid is LocalSystemSid or AdministratorsSid or TrustedInstallerSid;
}
