using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>
/// Verifies the fixed ProgramData installer-state chain and pins every protected descendant against
/// rename while an elevated writer owns the guard.
/// </summary>
public sealed class WindowsInstallerTransactionRootGuard :
    IInstallerTransactionRootGuard,
    IDisposable
{
    private const string ProductDirectoryName = "ClashSharp";
    private const string InstallerDirectoryName = "Installer";
    private const string StateVersionDirectoryName = "v2";

    private readonly object _gate = new();
    private readonly string _programDataPath;
    private readonly string _targetSid;
    private readonly IWindowsInstallerDirectoryNative _native;
    private readonly bool _createMissingProtectedDirectories;
    private List<IWindowsInstallerDirectoryLease>? _leases;
    private bool _disposed;

    private WindowsInstallerTransactionRootGuard(
        string programDataPath,
        string targetSid,
        IWindowsInstallerDirectoryNative native,
        bool createMissingProtectedDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSid);
        ArgumentNullException.ThrowIfNull(native);
        InstallerProtocolValidation.ValidateTargetSid(targetSid);

        _programDataPath = NormalizeDriveQualifiedPath(programDataPath);
        _targetSid = targetSid;
        _native = native;
        _createMissingProtectedDirectories = createMissingProtectedDirectories;
        RootPath = NormalizeDriveQualifiedPath(Path.Combine(
            _programDataPath,
            ProductDirectoryName,
            InstallerDirectoryName,
            StateVersionDirectoryName));
        if (!IsStrictDescendant(_programDataPath, RootPath))
        {
            throw new InstallerProtocolException(
                "installer.transaction.root_path_invalid");
        }
    }

    /// <summary>Gets the only machine-protected state root accepted by this guard.</summary>
    public string RootPath { get; }

    internal bool IsProtectedRootPresent
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _leases is not null
                    && _leases.Count == BuildDirectoryChain().Count();
            }
        }
    }

    /// <summary>Creates a guard bound to the Windows ProgramData known folder and one target user.</summary>
    /// <param name="targetSid">Canonical SID of the interactive user allowed to read recovery state.</param>
    /// <returns>A guard that owns rename-blocking directory handles until disposed.</returns>
    public static WindowsInstallerTransactionRootGuard CreateDefault(string targetSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The protected installer transaction root is available only on Windows.");
        }

        string programDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return new WindowsInstallerTransactionRootGuard(
            programDataPath,
            targetSid,
            new WindowsInstallerDirectoryNative(),
            createMissingProtectedDirectories: true);
    }

    /// <inheritdoc />
    public Task EnsureProtectedAsync(
        string absoluteRootPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string observedRoot = NormalizeDriveQualifiedPath(absoluteRootPath);
        if (!string.Equals(observedRoot, RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.transaction.root_path_invalid");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_leases is null)
                {
                    _leases = AcquireDirectoryChain(cancellationToken);
                }
                else
                {
                    RevalidateDirectoryChain(_leases, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InstallerProtocolException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                throw new InstallerProtocolException(
                    "installer.transaction.root_verification_failed",
                    exception);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Releases the directory handles that prevent root and ancestor rename.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            DisposeLeases(_leases);
            _leases = null;
            _disposed = true;
        }
    }

    internal static WindowsInstallerTransactionRootGuard CreateForTesting(
        string programDataPath,
        string targetSid,
        IWindowsInstallerDirectoryNative native) =>
        new(
            programDataPath,
            targetSid,
            native,
            createMissingProtectedDirectories: true);

    internal static WindowsInstallerTransactionRootGuard CreateReadOnlyDefault(
        string targetSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The protected installer transaction root is available only on Windows.");
        }

        string programDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return new WindowsInstallerTransactionRootGuard(
            programDataPath,
            targetSid,
            new WindowsInstallerDirectoryNative(),
            createMissingProtectedDirectories: false);
    }

    internal static WindowsInstallerTransactionRootGuard CreateReadOnlyForTesting(
        string programDataPath,
        string targetSid,
        IWindowsInstallerDirectoryNative native) =>
        new(
            programDataPath,
            targetSid,
            native,
            createMissingProtectedDirectories: false);

    private List<IWindowsInstallerDirectoryLease> AcquireDirectoryChain(
        CancellationToken cancellationToken)
    {
        var acquired = new List<IWindowsInstallerDirectoryLease>();
        try
        {
            foreach (WindowsInstallerDirectoryPath path in BuildDirectoryChain())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (path.CreateWithProtectedAcl
                    && _createMissingProtectedDirectories)
                {
                    _native.CreateDirectory(
                        path.Path,
                        WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(
                            _targetSid));
                }

                IWindowsInstallerDirectoryLease lease;
                try
                {
                    lease = _native.OpenDirectory(
                        path.Path,
                        preventRename: _createMissingProtectedDirectories
                            && path.CreateWithProtectedAcl);
                }
                catch (Exception exception) when (
                    !_createMissingProtectedDirectories
                    && path.CreateWithProtectedAcl
                    && IsMissingDirectory(exception))
                {
                    break;
                }
                acquired.Add(lease);
                ValidateObservation(
                    lease.Observe(),
                    path.RequiresExactProtection,
                    _targetSid);
            }

            RevalidateDirectoryChain(acquired, cancellationToken);
            return acquired;
        }
        catch
        {
            DisposeLeases(acquired);
            throw;
        }
    }

    private void RevalidateDirectoryChain(
        List<IWindowsInstallerDirectoryLease> leases,
        CancellationToken cancellationToken)
    {
        WindowsInstallerDirectoryPath[] paths = BuildDirectoryChain().ToArray();
        if (leases.Count > paths.Length)
        {
            throw new InstallerProtocolException(
                "installer.transaction.root_verification_failed");
        }

        for (int index = 0; index < leases.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateObservation(
                leases[index].Observe(),
                paths[index].RequiresExactProtection,
                _targetSid);
        }

        if (_createMissingProtectedDirectories || leases.Count == paths.Length)
        {
            if (leases.Count != paths.Length)
            {
                throw new InstallerProtocolException(
                    "installer.transaction.root_verification_failed");
            }

            return;
        }

        var appended = new List<IWindowsInstallerDirectoryLease>();
        try
        {
            for (int index = leases.Count; index < paths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IWindowsInstallerDirectoryLease lease;
                try
                {
                    lease = _native.OpenDirectory(
                        paths[index].Path,
                        preventRename: _createMissingProtectedDirectories
                            && paths[index].CreateWithProtectedAcl);
                }
                catch (Exception exception) when (IsMissingDirectory(exception))
                {
                    break;
                }

                appended.Add(lease);
                ValidateObservation(
                    lease.Observe(),
                    paths[index].RequiresExactProtection,
                    _targetSid);
            }

            if (appended.Count != 0)
            {
                leases.AddRange(appended);
                appended.Clear();
            }
        }
        finally
        {
            DisposeLeases(appended);
        }
    }

    private IEnumerable<WindowsInstallerDirectoryPath> BuildDirectoryChain()
    {
        string volumeRoot = Path.GetPathRoot(_programDataPath)
            ?? throw new InstallerProtocolException(
                "installer.transaction.root_path_invalid");
        string current = volumeRoot;
        yield return new WindowsInstallerDirectoryPath(current, RequiresExactProtection: false);

        foreach (string segment in RelativeSegments(volumeRoot, _programDataPath))
        {
            current = Path.Combine(current, segment);
            yield return new WindowsInstallerDirectoryPath(
                current,
                RequiresExactProtection: false);
        }

        current = _programDataPath;
        int protectedSegmentIndex = 0;
        foreach (string segment in RelativeSegments(_programDataPath, RootPath))
        {
            current = Path.Combine(current, segment);
            yield return new WindowsInstallerDirectoryPath(
                current,
                CreateWithProtectedAcl: true,
                RequiresExactProtection: protectedSegmentIndex > 0);
            protectedSegmentIndex++;
        }
    }

    private static IEnumerable<string> RelativeSegments(string parent, string descendant)
    {
        string relative = Path.GetRelativePath(parent, descendant);
        if (Path.IsPathRooted(relative)
            || relative is "." or ".."
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.transaction.root_path_invalid");
        }

        foreach (string segment in relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new InstallerProtocolException(
                    "installer.transaction.root_path_invalid");
            }

            yield return segment;
        }
    }

    private static void ValidateObservation(
        WindowsInstallerDirectoryObservation observation,
        bool requiresExactProtection,
        string targetSid)
    {
        if (!observation.IsDirectory)
        {
            throw new InstallerProtocolException(
                requiresExactProtection
                    ? "installer.transaction.root_not_directory"
                    : "installer.transaction.root_ancestor_not_directory");
        }

        if (observation.IsReparsePoint)
        {
            throw new InstallerProtocolException(
                requiresExactProtection
                    ? "installer.transaction.root_reparse_rejected"
                    : "installer.transaction.root_ancestor_reparse_rejected");
        }

        if (requiresExactProtection)
        {
            WindowsInstallerDirectorySecurityPolicy.ValidateProtectedRoot(
                observation.Security,
                targetSid);
        }
        else
        {
            WindowsInstallerDirectorySecurityPolicy.ValidateRenameAnchor(
                observation.Security);
        }
    }

    private static bool IsStrictDescendant(string parent, string descendant)
    {
        string prefix = Path.TrimEndingDirectorySeparator(parent)
            + Path.DirectorySeparatorChar;
        return descendant.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDriveQualifiedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            string candidate = Path.TrimEndingDirectorySeparator(path);
            if (!Path.IsPathFullyQualified(candidate)
                || candidate.Length < 3
                || !char.IsAsciiLetter(candidate[0])
                || candidate[1] != ':'
                || candidate[2] != Path.DirectorySeparatorChar)
            {
                throw new InstallerProtocolException(
                    "installer.transaction.root_path_invalid");
            }

            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            string? root = Path.GetPathRoot(fullPath);
            if (root is null
                || root.Length != 3
                || !char.IsAsciiLetter(root[0])
                || root[1] != ':'
                || root[2] != Path.DirectorySeparatorChar
                || fullPath.StartsWith("\\\\", StringComparison.Ordinal)
                || fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
                || fullPath.StartsWith("\\\\.\\", StringComparison.Ordinal)
                || !string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallerProtocolException(
                    "installer.transaction.root_path_invalid");
            }

            return fullPath;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            throw new InstallerProtocolException(
                "installer.transaction.root_path_invalid",
                exception);
        }
    }

    private static void DisposeLeases(
        IReadOnlyList<IWindowsInstallerDirectoryLease>? leases)
    {
        if (leases is null)
        {
            return;
        }

        for (int index = leases.Count - 1; index >= 0; index--)
        {
            leases[index].Dispose();
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        (exception is Win32Exception
            or IOException
            or UnauthorizedAccessException
            or SystemException)
        && exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);

    private static bool IsMissingDirectory(Exception exception) =>
        exception is DirectoryNotFoundException or FileNotFoundException
        || exception is Win32Exception { NativeErrorCode: 2 or 3 };

    private sealed record WindowsInstallerDirectoryPath(
        string Path,
        bool RequiresExactProtection,
        bool CreateWithProtectedAcl = false);
}

internal static class WindowsInstallerDirectorySecurityPolicy
{
    internal const string LocalSystemSid = "S-1-5-18";
    internal const string AdministratorsSid = "S-1-5-32-544";
    internal const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    internal const FileSystemRights TargetUserReadOnlyRights =
        FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    private const int GenericAll = 0x1000_0000;
    private const FileSystemRights DangerousAnchorRights =
        FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;
    private const InheritanceFlags ExpectedInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    private const AceFlags ExpectedAceFlags =
        AceFlags.ContainerInherit | AceFlags.ObjectInherit;

    internal static DirectorySecurity CreateProtectedDirectorySecurity(string targetSid)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        var localSystem = new SecurityIdentifier(LocalSystemSid);
        var administrators = new SecurityIdentifier(AdministratorsSid);
        var targetUser = new SecurityIdentifier(targetSid);
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(CreateRule(localSystem, FileSystemRights.FullControl));
        security.AddAccessRule(CreateRule(administrators, FileSystemRights.FullControl));
        security.AddAccessRule(CreateRule(targetUser, TargetUserReadOnlyRights));
        return security;
    }

    internal static void ValidateProtectedRoot(
        WindowsInstallerDirectorySecuritySnapshot security,
        string targetSid)
    {
        if (!security.HasDacl
            || !security.DaclProtected
            || !string.Equals(
                security.OwnerSid,
                AdministratorsSid,
                StringComparison.Ordinal)
            || security.AccessEntries.Count != 3
            || !HasExactRule(
                security.AccessEntries,
                LocalSystemSid,
                FileSystemRights.FullControl)
            || !HasExactRule(
                security.AccessEntries,
                AdministratorsSid,
                FileSystemRights.FullControl)
            || !HasExactRule(
                security.AccessEntries,
                targetSid,
                TargetUserReadOnlyRights))
        {
            throw new InstallerProtocolException(
                "installer.transaction.root_acl_invalid");
        }
    }

    internal static void ValidateRenameAnchor(
        WindowsInstallerDirectorySecuritySnapshot security)
    {
        if (!security.HasDacl
            || !IsTrustedAuthority(security.OwnerSid))
        {
            throw new InstallerProtocolException(
                "installer.transaction.root_ancestor_acl_invalid");
        }

        foreach (WindowsInstallerDirectoryAce entry in security.AccessEntries)
        {
            if (entry.Kind == WindowsInstallerDirectoryAceKind.Unsupported)
            {
                throw new InstallerProtocolException(
                    "installer.transaction.root_ancestor_acl_invalid");
            }

            bool appliesToAnchor = (entry.Flags & AceFlags.InheritOnly) == 0;
            if (entry.Kind == WindowsInstallerDirectoryAceKind.Allow
                && appliesToAnchor
                && !IsTrustedAuthority(entry.Sid)
                && HasDangerousAnchorRights(entry.AccessMask))
            {
                throw new InstallerProtocolException(
                    "installer.transaction.root_ancestor_acl_invalid");
            }
        }
    }

    private static FileSystemAccessRule CreateRule(
        SecurityIdentifier sid,
        FileSystemRights rights) =>
        new(
            sid,
            rights,
            ExpectedInheritance,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static bool HasExactRule(
        IReadOnlyList<WindowsInstallerDirectoryAce> entries,
        string sid,
        FileSystemRights rights)
    {
        WindowsInstallerDirectoryAce[] matches = entries
            .Where(entry => string.Equals(entry.Sid, sid, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            && matches[0].Kind == WindowsInstallerDirectoryAceKind.Allow
            && matches[0].AccessMask == (int)rights
            && matches[0].Flags == ExpectedAceFlags
            && !matches[0].IsObjectSpecific;
    }

    private static bool IsTrustedAuthority(string? sid) =>
        sid is LocalSystemSid or AdministratorsSid or TrustedInstallerSid;

    private static bool HasDangerousAnchorRights(int accessMask) =>
        // Windows 11 ProgramData intentionally grants Users create-folder/append-data rights.
        // Those can pre-position a name (which exact child validation rejects), but cannot replace
        // an existing protected child. Delete-child, delete, WRITE_DAC, and WRITE_OWNER can.
        (accessMask & ((int)DangerousAnchorRights | GenericAll)) != 0;
}

internal interface IWindowsInstallerDirectoryNative
{
    /// <summary>Creates a directory with the supplied protected descriptor.</summary>
    /// <param name="path">Canonical absolute directory path.</param>
    /// <param name="security">Exact descriptor applied during creation.</param>
    void CreateDirectory(string path, DirectorySecurity security);

    /// <summary>Opens a directory for observation and optionally pins its name against rename.</summary>
    /// <param name="path">Canonical absolute directory path.</param>
    /// <param name="preventRename">
    /// <see langword="true"/> to request DELETE access while withholding delete sharing; otherwise,
    /// <see langword="false"/> for a read-only observer that cannot require DELETE permission.
    /// </param>
    /// <returns>A lease owning the native directory handle.</returns>
    IWindowsInstallerDirectoryLease OpenDirectory(string path, bool preventRename);
}

internal interface IWindowsInstallerDirectoryLease : IDisposable
{
    WindowsInstallerDirectoryObservation Observe();
}

internal sealed record WindowsInstallerDirectoryObservation(
    bool IsDirectory,
    bool IsReparsePoint,
    WindowsInstallerDirectorySecuritySnapshot Security);

internal sealed record WindowsInstallerDirectorySecuritySnapshot(
    string? OwnerSid,
    bool HasDacl,
    bool DaclProtected,
    IReadOnlyList<WindowsInstallerDirectoryAce> AccessEntries);

internal sealed record WindowsInstallerDirectoryAce(
    string Sid,
    WindowsInstallerDirectoryAceKind Kind,
    int AccessMask,
    AceFlags Flags,
    bool IsObjectSpecific);

internal enum WindowsInstallerDirectoryAceKind
{
    Allow,
    Deny,
    Unsupported,
}
