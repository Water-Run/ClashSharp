using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>
/// Verifies the fixed ProgramData installer-state chain and pins every protected descendant against
/// rename while an elevated writer owns the guard.
/// </summary>
public sealed class WindowsInstallerTransactionRootGuard :
    IInstallerTransactionRootGuard,
    IDisposable
{
    private readonly object _gate = new();
    private readonly string _programDataPath;
    private readonly string? _targetSid;
    private readonly IWindowsInstallerDirectoryNative _native;
    private readonly bool _createMissingProtectedDirectories;
    private List<IWindowsInstallerDirectoryLease>? _leases;
    private bool _disposed;

    private WindowsInstallerTransactionRootGuard(
        string programDataPath,
        string? targetSid,
        IWindowsInstallerDirectoryNative native,
        bool createMissingProtectedDirectories,
        bool privateOwnerTransfer = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataPath);
        ArgumentNullException.ThrowIfNull(native);
        if (privateOwnerTransfer)
        {
            if (targetSid is not null)
            {
                throw new ArgumentException("Private transfer roots cannot carry a user-readable owner.", nameof(targetSid));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetSid);
            InstallerProtocolValidation.ValidateTargetSid(targetSid);
        }

        _programDataPath = NormalizeDriveQualifiedPath(programDataPath);
        _targetSid = targetSid;
        _native = native;
        _createMissingProtectedDirectories = createMissingProtectedDirectories;
        RootPath = NormalizeDriveQualifiedPath(Path.Combine(
            _programDataPath,
            InstallerStateLayout.ProductDirectoryName,
            privateOwnerTransfer ? InstallerOwnerTransferStateLayout.AuthorityDirectoryName : InstallerStateLayout.InstallerDirectoryName,
            privateOwnerTransfer ? InstallerOwnerTransferStateLayout.VersionDirectoryName : InstallerStateLayout.VersionDirectoryName));
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

    // Only the elevated owner-transfer persistence factory uses this fixed private layout.
    // The common product root must already exist; its current user's ACL is never rewritten.
    internal static WindowsInstallerTransactionRootGuard CreatePrivateOwnerTransferDefault() =>
        new(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            targetSid: null,
            new WindowsInstallerDirectoryNative(),
            createMissingProtectedDirectories: true,
            privateOwnerTransfer: true);

    internal static WindowsInstallerTransactionRootGuard CreatePrivateOwnerTransferForTesting(
        string programDataPath,
        IWindowsInstallerDirectoryNative native) =>
        new(programDataPath, null, native, createMissingProtectedDirectories: true, privateOwnerTransfer: true);

    internal static WindowsInstallerTransactionRootGuard CreateReadOnlyOwnerTransferDefault() =>
        new(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            targetSid: null,
            new WindowsInstallerDirectoryNative(),
            createMissingProtectedDirectories: false,
            privateOwnerTransfer: true);

    internal static WindowsInstallerTransactionRootGuard CreateReadOnlyOwnerTransferForTesting(
        string programDataPath,
        IWindowsInstallerDirectoryNative native) =>
        new(programDataPath, null, native, createMissingProtectedDirectories: false, privateOwnerTransfer: true);

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
                        _targetSid is null
                            ? WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity()
                            : WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(_targetSid));
                }

                IWindowsInstallerDirectoryLease lease;
                try
                {
                    lease = _native.OpenDirectory(path.Path);
                }
                catch (Exception exception) when (
                    !_createMissingProtectedDirectories
                    && path.AllowMissingWhenReadOnly
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
                    lease = _native.OpenDirectory(paths[index].Path);
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
                CreateWithProtectedAcl: _targetSid is not null || protectedSegmentIndex > 0,
                RequiresExactProtection: protectedSegmentIndex > 0,
                AllowMissingWhenReadOnly: true);
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
        WindowsDirectoryObservation observation,
        bool requiresExactProtection,
        string? targetSid)
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
            if (targetSid is null)
            {
                WindowsInstallerPrivateStateSecurity.Validate(observation.Security, directory: true);
            }
            else
            {
                WindowsInstallerDirectorySecurityPolicy.ValidateProtectedRoot(observation.Security, targetSid);
            }
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
        bool CreateWithProtectedAcl = false,
        bool AllowMissingWhenReadOnly = false);
}

internal static class WindowsInstallerDirectorySecurityPolicy
{
    internal const string LocalSystemSid = WindowsDirectoryAccessPolicy.LocalSystemSid;
    internal const string AdministratorsSid = WindowsDirectoryAccessPolicy.AdministratorsSid;
    internal const string TrustedInstallerSid = WindowsDirectoryAccessPolicy.TrustedInstallerSid;
    internal const FileSystemRights TargetUserReadOnlyRights = WindowsDirectoryAccessPolicy.OwnerReadOnlyRights;

    internal static DirectorySecurity CreateProtectedDirectorySecurity(string targetSid)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        return WindowsDirectoryAccessPolicy.CreateOwnerReadableDirectorySecurity(targetSid);
    }

    internal static void ValidateProtectedRoot(WindowsDirectorySecuritySnapshot security, string targetSid)
    {
        if (!WindowsDirectoryAccessPolicy.HasExactOwnerReadOnlyAccess(security, targetSid))
        {
            throw new InstallerProtocolException("installer.transaction.root_acl_invalid");
        }
    }

    internal static void ValidateRenameAnchor(WindowsDirectorySecuritySnapshot security)
    {
        if (!WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(security))
        {
            throw new InstallerProtocolException("installer.transaction.root_ancestor_acl_invalid");
        }
    }
}

internal interface IWindowsInstallerDirectoryNative
{
    /// <summary>Creates a directory with the supplied protected descriptor.</summary>
    /// <param name="path">Canonical absolute directory path.</param>
    /// <param name="security">Exact descriptor applied during creation.</param>
    void CreateDirectory(string path, DirectorySecurity security);

    /// <summary>
    /// Opens a directory for security observation and listing, withholding delete sharing
    /// to pin its name against deletion or rename without requesting delete authority.
    /// </summary>
    /// <param name="path">Canonical absolute directory path.</param>
    /// <returns>A lease owning the native directory handle.</returns>
    IWindowsInstallerDirectoryLease OpenDirectory(string path);
}

internal interface IWindowsInstallerDirectoryLease : IDisposable
{
    WindowsDirectoryObservation Observe();
}
