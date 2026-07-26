using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Infrastructure.Data;

/// <summary>Builds and validates the canonical version-one data-generation layout.</summary>
public sealed class DataGenerationPathPolicy
{
    /// <summary>Gets the fixed current-generation manifest filename.</summary>
    public const string CurrentManifestFileName = "current-generation.json";

    internal const string ManifestLockFileName = ".current-generation.lock";
    private const string DataDirectoryName = "Data";
    private const string SchemaDirectoryName = "v1";
    private const string GenerationsDirectoryName = "generations";
    private readonly string _applicationDataRoot;

    /// <summary>Initializes path policy without touching the filesystem.</summary>
    /// <param name="applicationDataRoot">Absolute application-local data root.</param>
    public DataGenerationPathPolicy(string applicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        if (!Path.IsPathFullyQualified(applicationDataRoot))
        {
            throw CreateUnsafePathException("The application data root must be absolute.");
        }

        _applicationDataRoot =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDataRoot));
        DataRootPath = Path.Combine(
            _applicationDataRoot,
            DataDirectoryName,
            SchemaDirectoryName);
        GenerationsRootPath = Path.Combine(DataRootPath, GenerationsDirectoryName);
        CurrentManifestPath = Path.Combine(DataRootPath, CurrentManifestFileName);
        CurrentManifestLockPath = Path.Combine(DataRootPath, ManifestLockFileName);
    }

    /// <summary>Gets the canonical version-one data root.</summary>
    public string DataRootPath { get; }

    /// <summary>Gets the canonical parent of all immutable generations.</summary>
    public string GenerationsRootPath { get; }

    /// <summary>Gets the canonical current-manifest path outside every generation.</summary>
    public string CurrentManifestPath { get; }

    internal string CurrentManifestLockPath { get; }

    /// <summary>Gets the canonical root for a nonempty generation identity.</summary>
    /// <param name="generationId">Stable generation identity.</param>
    /// <returns>The normalized absolute generation root.</returns>
    public string GetGenerationRootPath(Guid generationId)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException(
                "The generation identifier cannot be empty.",
                nameof(generationId));
        }

        return Path.Combine(GenerationsRootPath, generationId.ToString("N"));
    }

    /// <summary>Allocates one new immutable generation root and flushes its identity marker.</summary>
    /// <param name="generationId">Never-before-used stable generation identity.</param>
    /// <param name="generationNumber">Monotonically increasing generation number.</param>
    /// <returns>The validated descriptor for the newly allocated root.</returns>
    public DataGenerationDescriptor CreateGeneration(
        Guid generationId,
        long generationNumber)
    {
        string generationRoot = GetGenerationRootPath(generationId);
        DataGenerationDescriptor descriptor =
            new(generationId, generationNumber, generationRoot);
        EnsureLayout();
        if (Directory.Exists(generationRoot) || File.Exists(generationRoot))
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.DuplicateGeneration,
                "The generation identity or immutable root was already allocated.");
        }

        try
        {
            Directory.CreateDirectory(generationRoot);
            ValidateExistingPath(generationRoot);
            DataGenerationIdentityMarker.CreateAndFlush(descriptor);
            return descriptor;
        }
        catch (DataGenerationStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.Unavailable,
                "The immutable generation root could not be allocated.",
                exception);
        }
    }

    /// <summary>Ensures the canonical layout exists and contains no reparse points.</summary>
    public void EnsureLayout()
    {
        EnsureDirectoryHierarchy(
            GenerationsRootPath,
            Directory.Exists,
            File.Exists,
            static path => Directory.CreateDirectory(path),
            File.GetAttributes);
        ValidateExistingPath(DataRootPath);
        ValidateExistingPath(GenerationsRootPath);
        if (File.Exists(CurrentManifestPath))
        {
            ValidateExistingPath(CurrentManifestPath);
        }

        if (File.Exists(CurrentManifestLockPath))
        {
            ValidateExistingPath(CurrentManifestLockPath);
        }
    }

    /// <summary>Validates a staging path as absolute, contained, and on the canonical volume.</summary>
    /// <param name="stagingPath">Candidate staging path below the versioned data root.</param>
    /// <returns>The normalized safe staging path.</returns>
    public string ValidateStagingPath(string stagingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        if (!Path.IsPathFullyQualified(stagingPath))
        {
            throw CreateUnsafePathException("A generation staging path must be absolute.");
        }

        string normalized = Path.GetFullPath(stagingPath);
        if (!IsContainedBy(DataRootPath, normalized))
        {
            throw CreateUnsafePathException(
                "The generation staging path escapes the canonical data root.");
        }

        if (!string.Equals(
                Path.GetPathRoot(DataRootPath),
                Path.GetPathRoot(normalized),
                StringComparison.OrdinalIgnoreCase))
        {
            throw CreateUnsafePathException(
                "The generation staging path is on a different volume.");
        }

        string parent = Path.GetDirectoryName(normalized)
            ?? throw CreateUnsafePathException("The generation staging path has no parent.");
        ValidateExistingPath(parent);
        if (File.Exists(normalized) || Directory.Exists(normalized))
        {
            ValidateExistingPath(normalized);
        }

        return normalized;
    }

    /// <summary>Validates a descriptor against its exact canonical generation root.</summary>
    /// <param name="descriptor">Descriptor to validate.</param>
    public void ValidateDescriptor(DataGenerationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string expectedRoot = GetGenerationRootPath(descriptor.GenerationId);
        if (!string.Equals(
                expectedRoot,
                descriptor.RootPath,
                GetPathComparison()))
        {
            throw CreateUnsafePathException(
                "The descriptor root is not the canonical root for its generation identity.");
        }

        if (!Directory.Exists(descriptor.RootPath))
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.InvalidDescriptor,
                "The staged generation root does not exist.");
        }

        ValidateExistingPath(descriptor.RootPath);
        DataGenerationIdentityMarker.Validate(descriptor);
    }

    /// <summary>Determines whether a target is strictly below a supplied root.</summary>
    /// <param name="rootPath">Absolute containment root.</param>
    /// <param name="targetPath">Absolute target path.</param>
    /// <returns>True when the target remains strictly below the root.</returns>
    public static bool IsContainedBy(string rootPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!Path.IsPathFullyQualified(rootPath) || !Path.IsPathFullyQualified(targetPath))
        {
            return false;
        }

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string normalizedTarget = Path.GetFullPath(targetPath);
        string relative = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        return !string.Equals(relative, ".", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    internal static void ValidateNoReparsePoints(
        string path,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(getAttributes);
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((getAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw CreateUnsafePathException(
                    $"Data-generation path '{current}' is a reparse point.");
            }

            current = Directory.GetParent(current)?.FullName;
        }
    }

    internal static void EnsureDirectoryHierarchy(
        string targetPath,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        Action<string> createDirectory,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(createDirectory);
        ArgumentNullException.ThrowIfNull(getAttributes);
        Stack<string> hierarchy = new();
        DirectoryInfo? current = new(Path.GetFullPath(targetPath));
        while (current is not null)
        {
            hierarchy.Push(current.FullName);
            current = current.Parent;
        }

        while (hierarchy.TryPop(out string? path))
        {
            if (directoryExists(path))
            {
                ValidatePathComponent(path, getAttributes);
                continue;
            }

            if (fileExists(path))
            {
                throw CreateUnsafePathException(
                    $"Data-generation directory '{path}' is occupied by a file.");
            }

            createDirectory(path);
            if (!directoryExists(path))
            {
                throw CreateUnsafePathException(
                    $"Data-generation directory '{path}' was not created.");
            }

            ValidatePathComponent(path, getAttributes);
        }
    }

    internal string GetRelativeGenerationPath(Guid generationId)
    {
        return $"{GenerationsDirectoryName}/{generationId:N}";
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static DataGenerationStoreException CreateUnsafePathException(string message)
    {
        return new DataGenerationStoreException(DataGenerationStoreError.UnsafePath, message);
    }

    private static void ValidateExistingPath(string path)
    {
        ValidateNoReparsePoints(
            path,
            static current =>
            {
                try
                {
                    if (Directory.Exists(current) || File.Exists(current))
                    {
                        return File.GetAttributes(current);
                    }
                }
                catch (FileNotFoundException)
                {
                    // Atomic replacement may remove the old directory entry after Exists.
                }
                catch (DirectoryNotFoundException)
                {
                    // An ancestor can disappear between the existence and attribute probes.
                }

                return 0;
            });
    }

    private static void ValidatePathComponent(
        string path,
        Func<string, FileAttributes> getAttributes)
    {
        if ((getAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw CreateUnsafePathException(
                $"Data-generation path '{path}' is a reparse point.");
        }
    }
}
