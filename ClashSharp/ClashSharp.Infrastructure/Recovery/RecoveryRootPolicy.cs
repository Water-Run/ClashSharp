using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Infrastructure.Recovery;

/// <summary>Validates and protects the process-wide durable recovery root.</summary>
public static class RecoveryRootPolicy
{
    /// <summary>Gets the version-one recovery root below the current user's local application data.</summary>
    /// <returns>An absolute recovery root path.</returns>
    public static string GetDefaultRootPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashSharp",
            "Recovery",
            "v1");
    }

    /// <summary>Ensures a root exists, contains no reparse point, and has a restricted Windows ACL.</summary>
    /// <param name="rootPath">Absolute dedicated recovery root.</param>
    public static void EnsureProtectedRoot(string rootPath)
    {
        string normalizedRoot = NormalizeAbsoluteRoot(rootPath);
        Directory.CreateDirectory(normalizedRoot);
        ValidateNoReparsePoints(normalizedRoot, File.GetAttributes);
        if (OperatingSystem.IsWindows())
        {
            ApplyRestrictedWindowsAcl(normalizedRoot);
        }
    }

    /// <summary>Verifies a target path is contained by the recovery root and on the same volume.</summary>
    /// <param name="rootPath">Absolute dedicated recovery root.</param>
    /// <param name="targetPath">Target path that must remain below the root.</param>
    /// <returns>The normalized absolute target path.</returns>
    public static string ValidateContainedTarget(string rootPath, string targetPath)
    {
        string normalizedRoot = NormalizeAbsoluteRoot(rootPath);
        string normalizedTarget = Path.GetFullPath(targetPath);
        string relative = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new MutationJournalStoreException(
                MutationJournalStoreError.UnsafePath,
                "The recovery target escapes the dedicated recovery root.");
        }

        if (!string.Equals(
                Path.GetPathRoot(normalizedRoot),
                Path.GetPathRoot(normalizedTarget),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new MutationJournalStoreException(
                MutationJournalStoreError.UnsafePath,
                "The recovery target is not on the recovery root volume.");
        }

        return normalizedTarget;
    }

    internal static void ValidateNoReparsePoints(
        string rootPath,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentNullException.ThrowIfNull(getAttributes);
        string? current = NormalizeAbsoluteRoot(rootPath);
        while (current is not null)
        {
            FileAttributes attributes = getAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new MutationJournalStoreException(
                    MutationJournalStoreError.UnsafePath,
                    $"Recovery path '{current}' is a reparse point.");
            }

            current = Directory.GetParent(current)?.FullName;
        }
    }

    internal static string NormalizeAbsoluteRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new MutationJournalStoreException(
                MutationJournalStoreError.UnsafePath,
                "The recovery root must be an absolute path.");
        }

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (string.IsNullOrWhiteSpace(Path.GetPathRoot(normalized)))
        {
            throw new MutationJournalStoreException(
                MutationJournalStoreError.UnsafePath,
                "The recovery root must be absolute.");
        }

        return normalized;
    }

    private static void ApplyRestrictedWindowsAcl(string rootPath)
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new MutationJournalStoreException(
                MutationJournalStoreError.UnsafePath,
                "The current Windows user SID is unavailable.");
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            localSystem,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(rootPath).SetAccessControl(security);
    }
}
