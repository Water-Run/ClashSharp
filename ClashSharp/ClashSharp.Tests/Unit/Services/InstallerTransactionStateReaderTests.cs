extern alias ClashSharpUi;

using ClashSharp.Installer.Contracts;
using InstallerTransactionState =
    ClashSharpUi::ClashSharp.Service.InstallerTransactionState;
using InstallerTransactionStateReader =
    ClashSharpUi::ClashSharp.Service.InstallerTransactionStateReader;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies the App observes current and legacy journals without creating or repairing state.</summary>
public sealed class InstallerTransactionStateReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ClashSharp-InstallerTransactionReaderTests",
        Guid.NewGuid().ToString("N"));

    public InstallerTransactionStateReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    /// <summary>A legacy installation without the Installer directory remains launchable.</summary>
    [Fact]
    public void Read_InstallerDirectoryMissing_ReturnsClearWithoutCreatingState()
    {
        InstallerTransactionStateReader reader = new(_root);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Clear, result);
        Assert.False(Directory.Exists(ProductRoot));
    }

    /// <summary>Existing empty state directories remain launchable without creating a journal.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Read_EmptyStateDirectory_ReturnsClearWithoutCreatingState(int depth)
    {
        Directory.CreateDirectory(AncestorPath(depth));
        InstallerTransactionStateReader reader = new(_root);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Clear, result);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal(depth, Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Count());
    }

    /// <summary>Any readable journal blocks startup, including malformed or uncleared verified state.</summary>
    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "not-json-and-still-a-marker")]
    [InlineData(false, "{\"schemaVersion\":1}")]
    [InlineData(true, "")]
    [InlineData(true, "not-json-and-still-a-journal")]
    [InlineData(true, "{\"schema\":2,\"phase\":\"Verified\"}")]
    public void Read_OrdinaryJournal_ReturnsPending(bool current, string content)
    {
        string markerPath = MarkerPath(current);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, content);
        InstallerTransactionStateReader reader = new(_root);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Pending, result);
        Assert.Equal(content, File.ReadAllText(markerPath));
    }

    /// <summary>An unsafe ancestor cannot be mistaken for an absent journal.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Read_AncestorIsFile_ReturnsInvalid(int depth)
    {
        string path = AncestorPath(depth);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "collision");
        InstallerTransactionStateReader reader = new(_root);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Invalid, result);
        Assert.Equal("collision", File.ReadAllText(path));
    }

    /// <summary>A directory at either journal path fails closed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_JournalIsDirectory_ReturnsInvalid(bool current)
    {
        Directory.CreateDirectory(MarkerPath(current));
        InstallerTransactionStateReader reader = new(_root);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Invalid, result);
    }

    /// <summary>An existing journal that cannot be opened read-only fails closed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_JournalIsExclusivelyLocked_ReturnsInvalid(bool current)
    {
        string markerPath = MarkerPath(current);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "marker");
        InstallerTransactionStateReader reader = new(_root);
        using FileStream exclusive = new(
            markerPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Invalid, result);
    }

    /// <summary>A journal reparse point cannot redirect the fixed ProgramData read.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_JournalIsSymbolicLink_ReturnsInvalidWhenLinksAreAvailable(bool current)
    {
        string markerPath = MarkerPath(current);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        string target = Path.Combine(_root, "marker-target.json");
        File.WriteAllText(target, "marker");
        try
        {
            File.CreateSymbolicLink(markerPath, target);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException or
            NotSupportedException)
        {
            return;
        }

        try
        {
            InstallerTransactionStateReader reader = new(_root);

            InstallerTransactionState result = reader.Read();

            Assert.Equal(InstallerTransactionState.Invalid, result);
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    /// <summary>An empty redirected ancestor cannot hide transaction state behind a clear result.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Read_AncestorIsSymbolicLink_ReturnsInvalidWhenLinksAreAvailable(int depth)
    {
        string path = AncestorPath(depth);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string target = Path.Combine(_root, "directory-target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException or
            NotSupportedException)
        {
            return;
        }

        try
        {
            InstallerTransactionStateReader reader = new(_root);

            Assert.Equal(InstallerTransactionState.Invalid, reader.Read());
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    /// <summary>An old interrupted transaction remains blocking when the new state directory exists.</summary>
    [Fact]
    public void Read_LegacyMarkerBesideEmptyCurrentState_RemainsPending()
    {
        Directory.CreateDirectory(VersionRoot);
        File.WriteAllText(MarkerPath(current: false), "legacy");
        InstallerTransactionStateReader reader = new(_root);

        Assert.Equal(InstallerTransactionState.Pending, reader.Read());
        Assert.Empty(Directory.EnumerateFileSystemEntries(VersionRoot));
    }

    /// <summary>An injected relative root cannot resolve against the process working directory.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("ClashSharp")]
    public void Read_RootIsNotAbsolute_ReturnsInvalid(string root)
    {
        InstallerTransactionStateReader reader = new(root);

        Assert.Equal(InstallerTransactionState.Invalid, reader.Read());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ProductRoot => Path.Combine(
        _root,
        InstallerStateLayout.ProductDirectoryName);

    private string InstallerRoot => Path.Combine(
        ProductRoot,
        InstallerStateLayout.InstallerDirectoryName);

    private string VersionRoot => Path.Combine(
        InstallerRoot,
        InstallerStateLayout.VersionDirectoryName);

    private string MarkerPath(bool current) => current
        ? Path.Combine(VersionRoot, InstallerStateLayout.JournalFileName)
        : Path.Combine(InstallerRoot, InstallerStateLayout.LegacyMarkerFileName);

    private string AncestorPath(int depth) => depth switch
    {
        1 => ProductRoot,
        2 => InstallerRoot,
        3 => VersionRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(depth)),
    };
}
