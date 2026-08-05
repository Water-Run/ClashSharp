using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.ApplicationModel;

namespace ClashSharp.Service;

/// <summary>Validates the immutable executable paths that an elevated mihomo service will run.</summary>
internal interface IMihomoServiceBinaryTrustValidator
{
    /// <summary>Validates the service host and child executable trust chain.</summary>
    MihomoServiceBinaryTrustValidation Validate(string serviceHostPath, string mihomoBinaryPath);
}

/// <summary>Describes whether both LocalSystem executable paths have an administrator-owned trust chain.</summary>
internal readonly record struct MihomoServiceBinaryTrustValidation(
    bool IsTrusted,
    string? Component,
    string? Reason)
{
    /// <summary>Creates a successful validation result.</summary>
    public static MihomoServiceBinaryTrustValidation Trusted { get; } = new(true, null, null);

    /// <summary>Creates a fail-closed validation result.</summary>
    public static MihomoServiceBinaryTrustValidation Denied(string component, string reason)
    {
        return new MihomoServiceBinaryTrustValidation(false, component, reason);
    }
}

/// <summary>
/// Rejects LocalSystem executable paths that can be replaced by an untrusted local principal.
/// </summary>
/// <remarks>
/// The validator intentionally accepts write-capable ACL entries only for SYSTEM, built-in
/// administrators, and TrustedInstaller. For packaged installations, the package install
/// directory reported by Windows is an operating-system trust boundary; its inaccessible
/// parent (<c>WindowsApps</c>) is not inferred from a path prefix.
/// </remarks>
internal sealed class WindowsMihomoServiceBinaryTrustValidator : IMihomoServiceBinaryTrustValidator
{
    private const int MaximumPayloadEntryCount = 4096;

    private const uint GenericAll = 0x10000000;

    private const uint GenericWrite = 0x40000000;

    private const uint FileMutationRights =
        (uint)(FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership)
        | GenericAll
        | GenericWrite;

    private const uint ImmediateDirectoryMutationRights = FileMutationRights;

    private const uint AncestorReplacementRights =
        (uint)(FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership)
        | GenericAll;

    private static readonly HashSet<string> TrustedWriterSids = new(StringComparer.Ordinal)
    {
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        // NT SERVICE\TrustedInstaller
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    };

    private readonly IMihomoServicePathSecurityAccessor _securityAccessor;

    private readonly string? _protectedPackageRoot;

    /// <summary>Creates a validator backed by the live Windows filesystem and token.</summary>
    public WindowsMihomoServiceBinaryTrustValidator(string? protectedPackageRoot)
        : this(new WindowsMihomoServicePathSecurityAccessor(), protectedPackageRoot)
    {
    }

    /// <summary>Creates a validator with deterministic filesystem inspection for tests.</summary>
    internal WindowsMihomoServiceBinaryTrustValidator(
        IMihomoServicePathSecurityAccessor securityAccessor,
        string? protectedPackageRoot)
    {
        _securityAccessor = securityAccessor ?? throw new ArgumentNullException(nameof(securityAccessor));
        _protectedPackageRoot = NormalizeOptionalRoot(protectedPackageRoot);
    }

    /// <inheritdoc />
    public MihomoServiceBinaryTrustValidation Validate(
        string serviceHostPath,
        string mihomoBinaryPath)
    {
        MihomoServiceBinaryTrustValidation hostValidation = ValidateExecutable(
            "service host",
            serviceHostPath);
        if (!hostValidation.IsTrusted)
        {
            return hostValidation;
        }

        MihomoServiceBinaryTrustValidation servicePayloadValidation = ValidatePayloadDirectory(
            "service host payload",
            serviceHostPath);
        if (!servicePayloadValidation.IsTrusted)
        {
            return servicePayloadValidation;
        }

        MihomoServiceBinaryTrustValidation mihomoValidation = ValidateExecutable(
            "mihomo binary",
            mihomoBinaryPath);
        if (!mihomoValidation.IsTrusted)
        {
            return mihomoValidation;
        }

        return ValidatePayloadDirectory("mihomo payload", mihomoBinaryPath);
    }

    private MihomoServiceBinaryTrustValidation ValidatePayloadDirectory(
        string component,
        string executablePath)
    {
        string payloadRoot = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(payloadRoot);
        int inspectedEntryCount = 0;
        while (pendingDirectories.TryPop(out string? directory))
        {
            IReadOnlyList<string> entries;
            try
            {
                entries = _securityAccessor.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (IsInspectionFailure(exception))
            {
                return MihomoServiceBinaryTrustValidation.Denied(
                    component,
                    "service payload could not be enumerated");
            }

            foreach (string entry in entries)
            {
                inspectedEntryCount++;
                if (inspectedEntryCount > MaximumPayloadEntryCount || !IsDescendant(payloadRoot, entry))
                {
                    return MihomoServiceBinaryTrustValidation.Denied(
                        component,
                        "service payload exceeds its trusted directory boundary");
                }

                FileAttributes attributes;
                try
                {
                    attributes = _securityAccessor.GetAttributes(entry);
                }
                catch (Exception exception) when (IsInspectionFailure(exception))
                {
                    return MihomoServiceBinaryTrustValidation.Denied(
                        component,
                        "service payload metadata could not be inspected");
                }

                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                MihomoServiceBinaryTrustValidation validation = ValidatePathObject(
                    component,
                    entry,
                    isDirectory,
                    FileMutationRights,
                    isProtectedPackageBoundary: false);
                if (!validation.IsTrusted)
                {
                    return validation;
                }

                if (isDirectory)
                {
                    pendingDirectories.Push(entry);
                }
            }
        }

        return MihomoServiceBinaryTrustValidation.Trusted;
    }

    private MihomoServiceBinaryTrustValidation ValidateExecutable(string component, string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return MihomoServiceBinaryTrustValidation.Denied(component, "path is invalid");
        }

        bool fileExists;
        try
        {
            fileExists = _securityAccessor.FileExists(fullPath);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return MihomoServiceBinaryTrustValidation.Denied(
                component,
                "filesystem metadata could not be inspected");
        }

        if (!Path.IsPathFullyQualified(fullPath) || !fileExists)
        {
            return MihomoServiceBinaryTrustValidation.Denied(component, "file is missing");
        }

        string? packageBoundary = IsWithinProtectedPackageRoot(fullPath)
            ? _protectedPackageRoot
            : null;

        MihomoServiceBinaryTrustValidation fileValidation = ValidatePathObject(
            component,
            fullPath,
            isDirectory: false,
            FileMutationRights,
            isProtectedPackageBoundary: false);
        if (!fileValidation.IsTrusted)
        {
            return fileValidation;
        }

        string? directory = Path.GetDirectoryName(fullPath);
        bool isImmediateDirectory = true;
        while (directory is not null)
        {
            bool isPackageBoundary = packageBoundary is not null
                && PathEquals(directory, packageBoundary);
            uint dangerousRights = isImmediateDirectory
                ? ImmediateDirectoryMutationRights
                : AncestorReplacementRights;
            MihomoServiceBinaryTrustValidation directoryValidation = ValidatePathObject(
                component,
                directory,
                isDirectory: true,
                dangerousRights,
                isPackageBoundary);
            if (!directoryValidation.IsTrusted)
            {
                return directoryValidation;
            }

            if (isPackageBoundary)
            {
                break;
            }

            string? parent = Path.GetDirectoryName(directory);
            if (parent is null || PathEquals(parent, directory))
            {
                break;
            }

            directory = parent;
            isImmediateDirectory = false;
        }

        return MihomoServiceBinaryTrustValidation.Trusted;
    }

    private MihomoServiceBinaryTrustValidation ValidatePathObject(
        string component,
        string path,
        bool isDirectory,
        uint dangerousRights,
        bool isProtectedPackageBoundary)
    {
        FileAttributes attributes;
        try
        {
            attributes = _securityAccessor.GetAttributes(path);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return MihomoServiceBinaryTrustValidation.Denied(
                component,
                "filesystem metadata could not be inspected");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return MihomoServiceBinaryTrustValidation.Denied(
                component,
                "path contains a reparse point");
        }

        bool attributesDescribeDirectory = (attributes & FileAttributes.Directory) != 0;
        if (attributesDescribeDirectory != isDirectory)
        {
            return MihomoServiceBinaryTrustValidation.Denied(
                component,
                isDirectory ? "ancestor is not a directory" : "executable is not a regular file");
        }

        FileSystemSecurity security;
        try
        {
            security = _securityAccessor.GetAccessControl(path, isDirectory);
        }
        catch (UnauthorizedAccessException) when (isProtectedPackageBoundary)
        {
            // Package.Current supplies this boundary. Windows intentionally denies
            // ordinary users READ_CONTROL on portions of WindowsApps.
            return MihomoServiceBinaryTrustValidation.Trusted;
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return MihomoServiceBinaryTrustValidation.Denied(
                component,
                "filesystem ACL could not be inspected");
        }

        try
        {
            return ValidateSecurityDescriptor(component, security, dangerousRights);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return MihomoServiceBinaryTrustValidation.Denied(
                component,
                "filesystem ACL could not be inspected");
        }
    }

    private static MihomoServiceBinaryTrustValidation ValidateSecurityDescriptor(
        string component,
        FileSystemSecurity security,
        uint dangerousRights)
    {
        byte[] descriptorBytes = security.GetSecurityDescriptorBinaryForm();
        RawSecurityDescriptor descriptor = new(descriptorBytes, 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            return MihomoServiceBinaryTrustValidation.Denied(
                component,
                "path has an unrestricted null DACL");
        }

        IdentityReference? ownerReference = security.GetOwner(typeof(SecurityIdentifier));
        if (ownerReference is not SecurityIdentifier owner || !IsTrustedWriter(owner))
        {
            return MihomoServiceBinaryTrustValidation.Denied(
                component,
                "path owner is not SYSTEM, Administrators, or TrustedInstaller");
        }

        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (AuthorizationRule authorizationRule in rules)
        {
            if (authorizationRule is not FileSystemAccessRule rule
                || rule.AccessControlType != AccessControlType.Allow
                || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue;
            }

            SecurityIdentifier sid = (SecurityIdentifier)rule.IdentityReference;
            uint grantedRights = unchecked((uint)(int)rule.FileSystemRights);
            if (!IsTrustedWriter(sid) && (grantedRights & dangerousRights) != 0)
            {
                return MihomoServiceBinaryTrustValidation.Denied(
                    component,
                    "path grants modification rights to an untrusted principal");
            }
        }

        return MihomoServiceBinaryTrustValidation.Trusted;
    }

    private bool IsWithinProtectedPackageRoot(string path)
    {
        if (_protectedPackageRoot is null)
        {
            return false;
        }

        string relativePath = Path.GetRelativePath(_protectedPackageRoot, path);
        return !Path.IsPathFullyQualified(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsTrustedWriter(SecurityIdentifier sid)
    {
        return TrustedWriterSids.Contains(sid.Value);
    }

    private static bool IsInspectionFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or IdentityNotMappedException
            or PrivilegeNotHeldException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException;
    }

    private static string? NormalizeOptionalRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDescendant(string root, string path)
    {
        string relativePath = Path.GetRelativePath(root, path);
        return !Path.IsPathFullyQualified(relativePath)
            && !relativePath.Equals(".", StringComparison.Ordinal)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}

/// <summary>Reads live Windows filesystem security metadata.</summary>
internal interface IMihomoServicePathSecurityAccessor
{
    bool FileExists(string path);

    FileAttributes GetAttributes(string path);

    FileSystemSecurity GetAccessControl(string path, bool isDirectory);

    IReadOnlyList<string> EnumerateFileSystemEntries(string directory);
}

internal sealed class WindowsMihomoServicePathSecurityAccessor : IMihomoServicePathSecurityAccessor
{
    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public FileAttributes GetAttributes(string path)
    {
        return File.GetAttributes(path);
    }

    public FileSystemSecurity GetAccessControl(string path, bool isDirectory)
    {
        const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
        return isDirectory
            ? new DirectoryInfo(path).GetAccessControl(sections)
            : new FileInfo(path).GetAccessControl(sections);
    }

    public IReadOnlyList<string> EnumerateFileSystemEntries(string directory)
    {
        return Directory.EnumerateFileSystemEntries(directory).ToArray();
    }
}

/// <summary>Obtains an OS-asserted package installation trust boundary when package identity exists.</summary>
internal static class MihomoServicePackageTrust
{
    public static string? ResolveCurrentPackageInstallRoot()
    {
        try
        {
            return Package.Current.InstalledLocation.Path;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or COMException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
