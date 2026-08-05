using System.Buffers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.MihomoService;

internal sealed record MihomoStagedGeneration(
    long Generation,
    string ConfigurationHash,
    string ConfigurationPath);

internal sealed class MihomoConfigurationHashMismatchException : IOException
{
    internal MihomoConfigurationHashMismatchException()
        : base("The source configuration does not match the requested SHA-256 hash.")
    {
    }
}

internal sealed class MihomoGenerationConflictException : IOException
{
    internal MihomoGenerationConflictException(long generation)
        : base($"Runtime generation {generation} is already bound to different configuration bytes.")
    {
    }
}

/// <summary>Copies mutable App configuration bytes into a service-owned immutable generation.</summary>
internal sealed class MihomoGenerationStore
{
    private const int CopyBufferBytes = 64 * 1024;
    private const int RetainedGenerationCount = 8;
    private const int MaximumRuntimeEntryCount = 4096;

    private readonly MihomoServiceOptions _options;
    private readonly bool _protectDirectory;
    private readonly string? _commonApplicationDataRoot;

    internal MihomoGenerationStore(
        MihomoServiceOptions options,
        bool protectDirectory = true,
        string? commonApplicationDataRoot = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _protectDirectory = protectDirectory;
        _commonApplicationDataRoot = protectDirectory
            ? Path.GetFullPath(commonApplicationDataRoot ?? GetCommonApplicationDataRoot())
            : null;
    }

    internal async Task<MihomoStagedGeneration> StageAsync(
        long generation,
        string expectedHash,
        CancellationToken cancellationToken,
        string? retainedConfigurationPath = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);

        if (!MihomoServiceIpcProtocol.IsCanonicalSha256(expectedHash))
        {
            throw new ArgumentException("The expected hash is invalid.", nameof(expectedHash));
        }

        string runtimeDirectory = PrepareRuntimeDirectory();
        if (!File.Exists(_options.ConfigPath))
        {
            throw new FileNotFoundException(
                "The source mihomo configuration was not found.",
                _options.ConfigPath);
        }

        string finalPath = Path.Combine(
            _options.ServiceDataDirectory,
            $"generation-{generation:D20}-{expectedHash}.yaml");
        string generationPattern = $"generation-{generation:D20}-*.yaml";
        if (Directory.EnumerateFiles(_options.ServiceDataDirectory, generationPattern)
            .Any(path => !string.Equals(path, finalPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MihomoGenerationConflictException(generation);
        }

        if (File.Exists(finalPath))
        {
            SecureStagedFile(finalPath);
            await VerifyHashAsync(finalPath, expectedHash, cancellationToken).ConfigureAwait(false);
            await MihomoServiceConfigurationTrustValidator.ValidateAsync(
                    finalPath,
                    runtimeDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            PruneOldGenerations(finalPath, retainedConfigurationPath);
            return new MihomoStagedGeneration(generation, expectedHash, finalPath);
        }

        string temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string copiedHash = await CopyAndHashAsync(
                    _options.ConfigPath,
                    temporaryPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(copiedHash),
                    Convert.FromHexString(expectedHash)))
            {
                throw new MihomoConfigurationHashMismatchException();
            }

            await MihomoServiceConfigurationTrustValidator.ValidateAsync(
                    temporaryPath,
                    runtimeDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                File.Move(temporaryPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(temporaryPath);
                SecureStagedFile(finalPath);
                await VerifyHashAsync(finalPath, expectedHash, cancellationToken).ConfigureAwait(false);
            }

            SecureStagedFile(finalPath);
            PruneOldGenerations(finalPath, retainedConfigurationPath);
            return new MihomoStagedGeneration(generation, expectedHash, finalPath);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    internal static async Task VerifyHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        string actualHash = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(expectedHash)))
        {
            throw new MihomoConfigurationHashMismatchException();
        }
    }

    internal static async Task<string> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void PrepareServiceDirectory()
    {
        if (!_protectDirectory)
        {
            DirectoryInfo unprotectedDirectory = new(_options.ServiceDataDirectory);
            ValidateExistingDirectory(unprotectedDirectory, "generation endpoint");
            unprotectedDirectory.Create();
            unprotectedDirectory.Refresh();
            ValidateExistingDirectory(unprotectedDirectory, "generation endpoint");
            return;
        }

        string commonApplicationDataRoot = _commonApplicationDataRoot!;
        DirectoryInfo trustedRoot = new(commonApplicationDataRoot);
        if (!trustedRoot.Exists)
        {
            throw new DirectoryNotFoundException(
                "The trusted common application-data root does not exist.");
        }

        ValidateExistingDirectory(trustedRoot, "common application-data root");
        string productDirectoryPath = Path.Combine(commonApplicationDataRoot, "ClashSharp");
        string fixedServiceRootPath = Path.Combine(productDirectoryPath, "MihomoService");
        string expectedEndpointPath = Path.Combine(fixedServiceRootPath, _options.PipeName);
        if (!string.Equals(
                Path.GetFullPath(_options.ServiceDataDirectory),
                Path.GetFullPath(expectedEndpointPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The generation endpoint is outside the protected service data root.");
        }

        CreateAndProtectOwnedDirectory(productDirectoryPath, "product data root");
        CreateAndProtectOwnedDirectory(fixedServiceRootPath, "service data root");
        CreateAndProtectOwnedDirectory(expectedEndpointPath, "generation endpoint");
    }

    /// <summary>Creates and validates the LocalSystem-owned mihomo working directory.</summary>
    internal string PrepareRuntimeDirectory()
    {
        PrepareServiceDirectory();
        string runtimeDirectoryPath = _options.RuntimeDirectory;
        DirectoryInfo runtimeDirectory = new(runtimeDirectoryPath);
        ValidateExistingDirectory(runtimeDirectory, "runtime directory");
        runtimeDirectory.Create();
        runtimeDirectory.Refresh();
        ValidateExistingDirectory(runtimeDirectory, "runtime directory");
        if (_protectDirectory)
        {
            runtimeDirectory.SetAccessControl(CreateProtectedDirectorySecurity());
            runtimeDirectory.Refresh();
            ValidateExistingDirectory(runtimeDirectory, "runtime directory");
            ValidateProtectedRuntimeTree(runtimeDirectory);
        }
        else
        {
            ValidateRuntimeTreeHasNoReparsePoints(runtimeDirectory);
        }

        return runtimeDirectory.FullName;
    }

    private static void ValidateProtectedRuntimeTree(DirectoryInfo runtimeDirectory)
    {
        ValidateProtectedRuntimeObject(
            runtimeDirectory,
            runtimeDirectory.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner));

        int entryCount = 0;
        Stack<DirectoryInfo> pendingDirectories = new();
        pendingDirectories.Push(runtimeDirectory);
        while (pendingDirectories.TryPop(out DirectoryInfo? directory))
        {
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                entryCount++;
                if (entryCount > MaximumRuntimeEntryCount)
                {
                    throw new IOException("The protected runtime directory contains too many entries.");
                }

                entry.Refresh();
                ValidateOwnedDirectoryAttributes(entry.Attributes, "runtime entry");
                switch (entry)
                {
                    case DirectoryInfo childDirectory:
                        ValidateProtectedRuntimeObject(
                            childDirectory,
                            childDirectory.GetAccessControl(
                                AccessControlSections.Access | AccessControlSections.Owner));
                        pendingDirectories.Push(childDirectory);
                        break;
                    case FileInfo file:
                        ValidateProtectedRuntimeObject(
                            file,
                            file.GetAccessControl(
                                AccessControlSections.Access | AccessControlSections.Owner));
                        break;
                }
            }
        }
    }

    private static void ValidateRuntimeTreeHasNoReparsePoints(DirectoryInfo runtimeDirectory)
    {
        int entryCount = 0;
        Stack<DirectoryInfo> pendingDirectories = new();
        pendingDirectories.Push(runtimeDirectory);
        while (pendingDirectories.TryPop(out DirectoryInfo? directory))
        {
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                entryCount++;
                if (entryCount > MaximumRuntimeEntryCount)
                {
                    throw new IOException("The runtime directory contains too many entries.");
                }

                entry.Refresh();
                ValidateOwnedDirectoryAttributes(entry.Attributes, "runtime entry");
                if (entry is DirectoryInfo childDirectory)
                {
                    pendingDirectories.Push(childDirectory);
                }
            }
        }
    }

    private static void ValidateProtectedRuntimeObject(
        FileSystemInfo entry,
        FileSystemSecurity security)
    {
        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier ownerSid
            || !ownerSid.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            throw new UnauthorizedAccessException(
                $"The protected runtime {entry.Name} is not owned by LocalSystem.");
        }

        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        bool systemCanFullyControl = false;
        foreach (AuthorizationRule authorizationRule in rules)
        {
            if (authorizationRule is not FileSystemAccessRule rule
                || rule.IdentityReference is not SecurityIdentifier sid
                || (!sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            {
                throw new UnauthorizedAccessException(
                    $"The protected runtime {entry.Name} has an untrusted ACL.");
            }

            systemCanFullyControl |= sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.FullControl)
                    == FileSystemRights.FullControl;
        }

        if (!systemCanFullyControl)
        {
            throw new UnauthorizedAccessException(
                $"The protected runtime {entry.Name} does not grant LocalSystem full control.");
        }
    }

    private static void CreateAndProtectOwnedDirectory(string path, string role)
    {
        DirectoryInfo directory = new(path);
        ValidateExistingDirectory(directory, role);
        directory.Create();
        directory.Refresh();
        ValidateExistingDirectory(directory, role);
        directory.SetAccessControl(CreateProtectedDirectorySecurity());
        directory.Refresh();
        ValidateExistingDirectory(directory, role);
    }

    internal static DirectorySecurity CreateProtectedDirectorySecurity()
    {
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(localSystem);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            localSystem,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateProtectedFileSecurity()
    {
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(localSystem);
        security.AddAccessRule(new FileSystemAccessRule(
            localSystem,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private static void ValidateExistingDirectory(DirectoryInfo directory, string role)
    {
        directory.Refresh();
        if (directory.Exists)
        {
            ValidateOwnedDirectoryAttributes(directory.Attributes, role);
        }
    }

    internal static void ValidateOwnedDirectoryAttributes(FileAttributes attributes, string role)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"The {role} cannot be a reparse point.");
        }
    }

    private void SecureStagedFile(string path)
    {
        FileInfo file = new(path);
        file.Refresh();
        ValidateStagedFileAttributes(file.Attributes);
        if (_protectDirectory)
        {
            file.SetAccessControl(CreateProtectedFileSecurity());
            file.Refresh();
            ValidateStagedFileAttributes(file.Attributes);
        }

        file.Attributes |= FileAttributes.ReadOnly;
    }

    private static void ValidateStagedFileAttributes(FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("A staged configuration cannot be a reparse point.");
        }
    }

    private void PruneOldGenerations(
        string newlyStagedPath,
        string? retainedConfigurationPath)
    {
        string[] protectedPaths = retainedConfigurationPath is null
            ? [newlyStagedPath]
            : [newlyStagedPath, Path.GetFullPath(retainedConfigurationPath)];
        HashSet<string> retained = new(protectedPaths, StringComparer.OrdinalIgnoreCase);
        string[] candidates = Directory
            .EnumerateFiles(_options.ServiceDataDirectory, "generation-*.yaml")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        foreach (string path in candidates)
        {
            if (retained.Contains(path))
            {
                continue;
            }

            if (retained.Count < RetainedGenerationCount)
            {
                retained.Add(path);
                continue;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static string GetCommonApplicationDataRoot()
    {
        string root = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrWhiteSpace(root)
            ? throw new InvalidOperationException(
                "The common application-data directory is unavailable.")
            : root;
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
