extern alias ClashSharpUi;

using InstallerTransactionState =
    ClashSharpUi::ClashSharp.Service.InstallerTransactionState;
using InstallerTransactionStateReader =
    ClashSharpUi::ClashSharp.Service.InstallerTransactionStateReader;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies the App observes the Installer public marker without creating or repairing it.</summary>
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

    /// <summary>Any ordinary readable public marker blocks startup without interpreting its content.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-json-and-still-a-marker")]
    [InlineData("{\"schemaVersion\":1}")]
    public void Read_OrdinaryPublicMarker_ReturnsPending(string content)
    {
        Directory.CreateDirectory(InstallerRoot);
        File.WriteAllText(MarkerPath, content);
        InstallerTransactionStateReader reader = new(_root);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Pending, result);
        Assert.Equal(content, File.ReadAllText(MarkerPath));
    }

    /// <summary>An unsafe product path cannot be mistaken for an absent transaction marker.</summary>
    [Fact]
    public void Read_ProductPathIsFile_ReturnsInvalid()
    {
        File.WriteAllText(ProductRoot, "collision");
        InstallerTransactionStateReader reader = new(_root);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Invalid, result);
    }

    /// <summary>A directory at the public marker path fails closed.</summary>
    [Fact]
    public void Read_PublicMarkerIsDirectory_ReturnsInvalid()
    {
        Directory.CreateDirectory(MarkerPath);
        InstallerTransactionStateReader reader = new(_root);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Invalid, result);
    }

    /// <summary>An existing marker that cannot be opened read-only fails closed.</summary>
    [Fact]
    public void Read_PublicMarkerIsExclusivelyLocked_ReturnsInvalid()
    {
        Directory.CreateDirectory(InstallerRoot);
        File.WriteAllText(MarkerPath, "marker");
        InstallerTransactionStateReader reader = new(_root);
        using FileStream exclusive = new(
            MarkerPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        InstallerTransactionState result = reader.Read();

        Assert.Equal(InstallerTransactionState.Invalid, result);
    }

    /// <summary>A reparse-point public marker cannot redirect the fixed ProgramData read.</summary>
    [Fact]
    public void Read_PublicMarkerIsSymbolicLink_ReturnsInvalidWhenLinksAreAvailable()
    {
        Directory.CreateDirectory(InstallerRoot);
        string target = Path.Combine(_root, "marker-target.json");
        File.WriteAllText(target, "marker");
        try
        {
            File.CreateSymbolicLink(MarkerPath, target);
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
            File.Delete(MarkerPath);
        }
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
        InstallerTransactionStateReader.ProductDirectoryName);

    private string InstallerRoot => Path.Combine(
        ProductRoot,
        InstallerTransactionStateReader.InstallerDirectoryName);

    private string MarkerPath => Path.Combine(
        InstallerRoot,
        InstallerTransactionStateReader.PublicMarkerFileName);
}
