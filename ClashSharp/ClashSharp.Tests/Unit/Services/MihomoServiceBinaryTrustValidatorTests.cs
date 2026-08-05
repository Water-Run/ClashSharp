using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests the LocalSystem executable deployment trust boundary.</summary>
public sealed class MihomoServiceBinaryTrustValidatorTests
{
    private static readonly SecurityIdentifier SystemSid = new(
        WellKnownSidType.LocalSystemSid,
        null);

    private static readonly SecurityIdentifier AdministratorsSid = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);

    private static readonly SecurityIdentifier UsersSid = new(
        WellKnownSidType.BuiltinUsersSid,
        null);

    /// <summary>Verifies administrator-owned files below a protected package boundary are accepted.</summary>
    [Fact]
    public void Validate_ProtectedTrustedTree_IsAccepted()
    {
        const string packageRoot = @"C:\Program Files\WindowsApps\ClashSharp_1";
        const string serviceHost = packageRoot + @"\Binaries\Service\ClashSharp.MihomoService.exe";
        const string mihomo = packageRoot + @"\Binaries\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(packageRoot, serviceHost, mihomo);
        WindowsMihomoServiceBinaryTrustValidator validator = new(accessor, packageRoot);

        MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

        Assert.True(result.IsTrusted, $"{result.Component}: {result.Reason}");
    }

    /// <summary>Verifies any reparse point in the executable path is rejected.</summary>
    [Fact]
    public void Validate_ReparsePointExecutable_IsRejected()
    {
        const string packageRoot = @"C:\Program Files\WindowsApps\ClashSharp_1";
        const string serviceHost = packageRoot + @"\Binaries\Service\ClashSharp.MihomoService.exe";
        const string mihomo = packageRoot + @"\Binaries\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(packageRoot, serviceHost, mihomo);
        accessor.SetAttributes(serviceHost, FileAttributes.Normal | FileAttributes.ReparsePoint);
        WindowsMihomoServiceBinaryTrustValidator validator = new(accessor, packageRoot);

        MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

        Assert.False(result.IsTrusted);
        Assert.Equal("service host", result.Component);
        Assert.Equal("path contains a reparse point", result.Reason);
    }

    /// <summary>Verifies a directory that lets ordinary users add service sidecars is rejected.</summary>
    [Fact]
    public void Validate_WritableImmediateDirectory_IsRejected()
    {
        const string packageRoot = @"C:\Program Files\WindowsApps\ClashSharp_1";
        const string serviceDirectory = packageRoot + @"\Binaries\Service";
        const string serviceHost = serviceDirectory + @"\ClashSharp.MihomoService.exe";
        const string mihomo = packageRoot + @"\Binaries\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(packageRoot, serviceHost, mihomo);
        DirectorySecurity writableSecurity = CreateTrustedDirectorySecurity();
        writableSecurity.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            FileSystemRights.WriteData,
            AccessControlType.Allow));
        accessor.SetSecurity(serviceDirectory, writableSecurity);
        WindowsMihomoServiceBinaryTrustValidator validator = new(accessor, packageRoot);

        MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

        Assert.False(result.IsTrusted);
        Assert.Equal("path grants modification rights to an untrusted principal", result.Reason);
    }

    /// <summary>Verifies a writable sibling DLL in the self-contained service payload is rejected.</summary>
    [Fact]
    public void Validate_WritableSiblingDll_IsRejected()
    {
        const string packageRoot = @"C:\Program Files\WindowsApps\ClashSharp_1";
        const string serviceDirectory = packageRoot + @"\Binaries\Service";
        const string serviceHost = serviceDirectory + @"\ClashSharp.MihomoService.exe";
        const string mihomo = packageRoot + @"\Binaries\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(packageRoot, serviceHost, mihomo);
        FileSecurity writableLibrary = CreateTrustedFileSecurity();
        writableLibrary.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            FileSystemRights.WriteData,
            AccessControlType.Allow));
        accessor.AddFile(serviceDirectory + @"\ClashSharp.MihomoService.dll", writableLibrary);
        WindowsMihomoServiceBinaryTrustValidator validator = new(accessor, packageRoot);

        MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

        Assert.False(result.IsTrusted);
        Assert.Equal("service host payload", result.Component);
        Assert.Equal("path grants modification rights to an untrusted principal", result.Reason);
    }

    /// <summary>Verifies every ACL right that can replace an existing ancestor is rejected.</summary>
    [Theory]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void Validate_ReplaceableAncestor_IsRejected(FileSystemRights replacementRight)
    {
        const string trustedRoot = @"C:\Protected";
        const string serviceHost = trustedRoot + @"\App\Service\service.exe";
        const string mihomo = trustedRoot + @"\App\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(@"C:\", serviceHost, mihomo);
        DirectorySecurity replaceableSecurity = CreateTrustedDirectorySecurity();
        replaceableSecurity.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            replacementRight,
            AccessControlType.Allow));
        accessor.SetSecurity(trustedRoot, replaceableSecurity);
        WindowsMihomoServiceBinaryTrustValidator validator = new(accessor, protectedPackageRoot: null);

        MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

        Assert.False(result.IsTrusted);
        Assert.Equal("service host", result.Component);
        Assert.Equal("path grants modification rights to an untrusted principal", result.Reason);
    }

    /// <summary>Verifies an untrusted owner is rejected because owners can rewrite the DACL.</summary>
    [Fact]
    public void Validate_UntrustedOwner_IsRejected()
    {
        const string packageRoot = @"C:\Program Files\WindowsApps\ClashSharp_1";
        const string serviceHost = packageRoot + @"\Binaries\Service\service.exe";
        const string mihomo = packageRoot + @"\Binaries\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(packageRoot, serviceHost, mihomo);
        FileSecurity userOwnedSecurity = CreateTrustedFileSecurity();
        userOwnedSecurity.SetOwner(UsersSid);
        accessor.SetSecurity(serviceHost, userOwnedSecurity);
        WindowsMihomoServiceBinaryTrustValidator validator = new(accessor, packageRoot);

        MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

        Assert.False(result.IsTrusted);
        Assert.Equal(
            "path owner is not SYSTEM, Administrators, or TrustedInstaller",
            result.Reason);
    }

    /// <summary>Verifies an inherit-only ACE is not treated as an effective grant on its directory.</summary>
    [Fact]
    public void Validate_InheritOnlyWriteAce_DoesNotGrantCurrentDirectoryAccess()
    {
        const string packageRoot = @"C:\Program Files\WindowsApps\ClashSharp_1";
        const string serviceHost = packageRoot + @"\Binaries\Service\service.exe";
        const string mihomo = packageRoot + @"\Binaries\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(packageRoot, serviceHost, mihomo);
        DirectorySecurity boundarySecurity = CreateTrustedDirectorySecurity();
        boundarySecurity.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly,
            AccessControlType.Allow));
        accessor.SetSecurity(packageRoot, boundarySecurity);
        WindowsMihomoServiceBinaryTrustValidator validator = new(accessor, packageRoot);

        MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

        Assert.True(result.IsTrusted, result.Reason);
    }

    /// <summary>Verifies deny-plus-allow ACLs remain fail closed regardless of ACE ordering.</summary>
    [Fact]
    public void Validate_DenyAndAllowWriteAces_IsConservativelyRejected()
    {
        const string packageRoot = @"C:\Program Files\WindowsApps\ClashSharp_1";
        const string serviceDirectory = packageRoot + @"\Binaries\Service";
        const string serviceHost = serviceDirectory + @"\service.exe";
        const string mihomo = packageRoot + @"\Binaries\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(packageRoot, serviceHost, mihomo);
        DirectorySecurity security = CreateTrustedDirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            FileSystemRights.WriteData,
            AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            FileSystemRights.WriteData,
            AccessControlType.Allow));
        accessor.SetSecurity(serviceDirectory, security);
        WindowsMihomoServiceBinaryTrustValidator validator = new(accessor, packageRoot);

        MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

        Assert.False(result.IsTrusted);
        Assert.Equal("path grants modification rights to an untrusted principal", result.Reason);
    }

    /// <summary>Verifies WindowsApps ACL opacity is accepted only at the OS-asserted package boundary.</summary>
    [Fact]
    public void Validate_InaccessiblePackageBoundary_IsAcceptedOnlyWhenAssertedByOs()
    {
        const string packageRoot = @"C:\Program Files\WindowsApps\ClashSharp_1";
        const string serviceHost = packageRoot + @"\Binaries\Service\service.exe";
        const string mihomo = packageRoot + @"\Binaries\mihomo.exe";
        FakePathSecurityAccessor accessor = CreateTrustedTree(packageRoot, serviceHost, mihomo);
        accessor.SetAccessControlFailure(packageRoot, new UnauthorizedAccessException("WindowsApps ACL"));

        MihomoServiceBinaryTrustValidation packagedResult =
            new WindowsMihomoServiceBinaryTrustValidator(accessor, packageRoot)
                .Validate(serviceHost, mihomo);
        MihomoServiceBinaryTrustValidation unpackagedResult =
            new WindowsMihomoServiceBinaryTrustValidator(accessor, protectedPackageRoot: null)
                .Validate(serviceHost, mihomo);

        Assert.True(packagedResult.IsTrusted, packagedResult.Reason);
        Assert.False(unpackagedResult.IsTrusted);
        Assert.Equal("filesystem ACL could not be inspected", unpackagedResult.Reason);
    }

    /// <summary>Verifies an ordinary user-owned development output is rejected by the live ACL reader.</summary>
    [Fact]
    public void Validate_UserOwnedTemporaryExecutables_AreRejected()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.BinaryTrust." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string serviceHost = Path.Combine(temporaryDirectory, "service.exe");
        string mihomo = Path.Combine(temporaryDirectory, "mihomo.exe");
        File.WriteAllBytes(serviceHost, [0x4d, 0x5a]);
        File.WriteAllBytes(mihomo, [0x4d, 0x5a]);

        try
        {
            WindowsMihomoServiceBinaryTrustValidator validator = new(protectedPackageRoot: null);

            MihomoServiceBinaryTrustValidation result = validator.Validate(serviceHost, mihomo);

            Assert.False(result.IsTrusted);
        }
        finally
        {
            File.Delete(serviceHost);
            File.Delete(mihomo);
            Directory.Delete(temporaryDirectory);
        }
    }

    private static FakePathSecurityAccessor CreateTrustedTree(
        string boundary,
        string serviceHost,
        string mihomo)
    {
        FakePathSecurityAccessor accessor = new();
        string fullBoundary = Path.GetFullPath(boundary);
        accessor.AddDirectory(fullBoundary, CreateTrustedDirectorySecurity());

        AddDirectoriesBetween(accessor, fullBoundary, Path.GetDirectoryName(serviceHost)!);
        AddDirectoriesBetween(accessor, fullBoundary, Path.GetDirectoryName(mihomo)!);
        accessor.AddFile(serviceHost, CreateTrustedFileSecurity());
        accessor.AddFile(mihomo, CreateTrustedFileSecurity());
        return accessor;
    }

    private static void AddDirectoriesBetween(
        FakePathSecurityAccessor accessor,
        string boundary,
        string directory)
    {
        Stack<string> missingDirectories = new();
        string current = Path.GetFullPath(directory);
        while (!PathEquals(current, boundary))
        {
            missingDirectories.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException("Test path escaped its boundary.");
        }

        while (missingDirectories.TryPop(out string? missingDirectory))
        {
            accessor.AddDirectory(missingDirectory, CreateTrustedDirectorySecurity());
        }
    }

    private static FileSecurity CreateTrustedFileSecurity()
    {
        FileSecurity security = new();
        security.SetOwner(SystemSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        return security;
    }

    private static DirectorySecurity CreateTrustedDirectorySecurity()
    {
        DirectorySecurity security = new();
        security.SetOwner(SystemSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakePathSecurityAccessor : IMihomoServicePathSecurityAccessor
    {
        private readonly Dictionary<string, PathEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path)
        {
            return _entries.TryGetValue(Normalize(path), out PathEntry? entry)
                && (entry.Attributes & FileAttributes.Directory) == 0;
        }

        public FileAttributes GetAttributes(string path)
        {
            return _entries[Normalize(path)].Attributes;
        }

        public FileSystemSecurity GetAccessControl(string path, bool isDirectory)
        {
            PathEntry entry = _entries[Normalize(path)];
            if (entry.AccessControlFailure is not null)
            {
                throw entry.AccessControlFailure;
            }

            return entry.Security;
        }

        public IReadOnlyList<string> EnumerateFileSystemEntries(string directory)
        {
            string normalizedDirectory = Normalize(directory);
            return _entries.Keys
                .Where(path => Path.GetDirectoryName(path) is string parent
                    && PathEquals(parent, normalizedDirectory))
                .ToArray();
        }

        public void AddDirectory(string path, DirectorySecurity security)
        {
            _entries[Normalize(path)] = new PathEntry(FileAttributes.Directory, security);
        }

        public void AddFile(string path, FileSecurity security)
        {
            _entries[Normalize(path)] = new PathEntry(FileAttributes.Normal, security);
        }

        public void SetAttributes(string path, FileAttributes attributes)
        {
            _entries[Normalize(path)].Attributes = attributes;
        }

        public void SetSecurity(string path, FileSystemSecurity security)
        {
            _entries[Normalize(path)].Security = security;
        }

        public void SetAccessControlFailure(string path, Exception exception)
        {
            _entries[Normalize(path)].AccessControlFailure = exception;
        }

        private static string Normalize(string path)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        private sealed class PathEntry(FileAttributes attributes, FileSystemSecurity security)
        {
            public FileAttributes Attributes { get; set; } = attributes;

            public FileSystemSecurity Security { get; set; } = security;

            public Exception? AccessControlFailure { get; set; }
        }
    }
}
