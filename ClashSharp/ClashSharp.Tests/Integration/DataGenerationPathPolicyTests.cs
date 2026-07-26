using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies generation roots and manifest paths cannot escape their canonical layout.</summary>
public sealed class DataGenerationPathPolicyTests
{
    /// <summary>Verifies canonical versioned paths derive only from a stable generation identity.</summary>
    [Fact]
    public async Task Paths_ValidIdentity_AreCanonicalAndSeparated()
    {
        await using DataGenerationTestDirectory directory = new();
        Guid generationId = Guid.Parse("a9293a65-71ac-4409-ab17-c5cba3625367");
        string expectedGenerationRoot = Path.Combine(
            directory.RootPath,
            "Data",
            "v1",
            "generations",
            generationId.ToString("N"));

        Assert.Equal(expectedGenerationRoot, directory.Policy.GetGenerationRootPath(generationId));
        Assert.Equal(
            Path.Combine(directory.RootPath, "Data", "v1", "current-generation.json"),
            directory.Policy.CurrentManifestPath);
        Assert.False(
            DataGenerationPathPolicy.IsContainedBy(
                directory.Policy.GenerationsRootPath,
                directory.Policy.CurrentManifestPath));
    }

    /// <summary>Verifies relative application roots are rejected before normalization can hide them.</summary>
    [Fact]
    public void Constructor_RelativeRoot_IsRejected()
    {
        DataGenerationStoreException exception = Assert.Throws<DataGenerationStoreException>(
            () => new DataGenerationPathPolicy("relative-data-root"));

        Assert.Equal(DataGenerationStoreError.UnsafePath, exception.Error);
    }

    /// <summary>Verifies empty identities cannot form descriptors or paths.</summary>
    [Fact]
    public async Task EmptyGenerationIdentity_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();

        Assert.Throws<ArgumentException>(
            () => directory.Policy.GetGenerationRootPath(Guid.Empty));
        Assert.Throws<ArgumentException>(
            () => new DataGenerationDescriptor(Guid.Empty, 1, directory.RootPath));
    }

    /// <summary>Verifies relative, escaping, and cross-volume staging paths are rejected.</summary>
    [Fact]
    public async Task ValidateStagingPath_UnsafePath_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        string escapingPath = Path.GetFullPath(Path.Combine(directory.Policy.DataRootPath, "..", "escape.tmp"));
        string root = Path.GetPathRoot(directory.RootPath)!;
        string alternateRoot = string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase)
            ? @"D:\"
            : @"C:\";
        string crossVolumePath = Path.Combine(alternateRoot, "ClashSharp-stage.tmp");

        AssertUnsafe(() => directory.Policy.ValidateStagingPath("relative.tmp"));
        AssertUnsafe(() => directory.Policy.ValidateStagingPath(escapingPath));
        AssertUnsafe(() => directory.Policy.ValidateStagingPath(crossVolumePath));
    }

    /// <summary>Verifies any reparse point in the data path is rejected.</summary>
    [Fact]
    public async Task ValidateNoReparsePoints_ReparseAncestor_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();

        DataGenerationStoreException exception = Assert.Throws<DataGenerationStoreException>(
            () => DataGenerationPathPolicy.ValidateNoReparsePoints(
                directory.Policy.DataRootPath,
                _ => FileAttributes.ReparsePoint));

        Assert.Equal(DataGenerationStoreError.UnsafePath, exception.Error);
    }

    /// <summary>Verifies layout creation stops before writing below an existing reparse ancestor.</summary>
    [Fact]
    public void EnsureDirectoryHierarchy_ReparseAncestor_DoesNotCreateChildren()
    {
        string applicationRoot = Path.Combine(
            Path.GetTempPath(),
            $"ClashSharp-Path-Policy-{Guid.NewGuid():N}");
        string reparseAncestor = Path.Combine(applicationRoot, "Data");
        string target = Path.Combine(reparseAncestor, "v1", "generations");
        HashSet<string> existing = new(GetPathComparer());
        DirectoryInfo? current = new(reparseAncestor);
        while (current is not null)
        {
            existing.Add(current.FullName);
            current = current.Parent;
        }

        List<string> created = [];
        DataGenerationStoreException exception =
            Assert.Throws<DataGenerationStoreException>(
                () => DataGenerationPathPolicy.EnsureDirectoryHierarchy(
                    target,
                    existing.Contains,
                    _ => false,
                    created.Add,
                    path => PathsEqual(path, reparseAncestor)
                        ? FileAttributes.ReparsePoint
                        : FileAttributes.Directory));

        Assert.Equal(DataGenerationStoreError.UnsafePath, exception.Error);
        Assert.Empty(created);
    }

    /// <summary>Verifies an identity marker symlink cannot redirect validation outside its generation.</summary>
    [Fact]
    public async Task ValidateDescriptor_ReparseIdentityMarker_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor descriptor = directory.CreateGeneration(1);
        string markerPath = Path.Combine(
            descriptor.RootPath,
            DataGenerationIdentityMarker.FileName);
        string externalMarkerPath = Path.Combine(
            directory.RootPath,
            "external-generation-identity.json");
        File.Copy(markerPath, externalMarkerPath);
        File.Delete(markerPath);
        File.CreateSymbolicLink(markerPath, externalMarkerPath);

        DataGenerationStoreException exception =
            Assert.Throws<DataGenerationStoreException>(
                () => directory.Policy.ValidateDescriptor(descriptor));

        Assert.Equal(DataGenerationStoreError.UnsafePath, exception.Error);
    }

    /// <summary>Verifies a current-manifest symlink cannot redirect store reads.</summary>
    [Fact]
    public async Task LoadCurrentAsync_ReparseManifest_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        await directory.PromoteFirstAsync();
        string externalManifestPath = Path.Combine(
            directory.RootPath,
            "external-current-generation.json");
        File.Copy(directory.Policy.CurrentManifestPath, externalManifestPath);
        File.Delete(directory.Policy.CurrentManifestPath);
        File.CreateSymbolicLink(
            directory.Policy.CurrentManifestPath,
            externalManifestPath);

        DataGenerationStoreException exception =
            await Assert.ThrowsAsync<DataGenerationStoreException>(
                () => directory.Store.LoadCurrentAsync(CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.UnsafePath, exception.Error);
    }

    /// <summary>Verifies manifest snapshots reject malformed hashes and inconsistent counters.</summary>
    [Theory]
    [InlineData(0, 1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(1, 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(1, 1, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData(1, 1, "short")]
    public async Task ManifestSnapshot_InvalidFields_AreRejected(
        long manifestRevision,
        long highestGenerationNumber,
        string hash)
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor descriptor = directory.CreateGeneration(1);

        Assert.ThrowsAny<ArgumentException>(
            () => new DataGenerationManifestSnapshot(
                descriptor,
                manifestRevision,
                highestGenerationNumber,
                hash));
    }

    private static void AssertUnsafe(Action action)
    {
        DataGenerationStoreException exception = Assert.Throws<DataGenerationStoreException>(action);
        Assert.Equal(DataGenerationStoreError.UnsafePath, exception.Error);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(left, right, GetPathComparison());
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
