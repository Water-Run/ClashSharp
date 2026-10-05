using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenancePackageStateLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClashSharp.PackageEvidence." + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EveryFileRemainsProtectedUntilLeaseDisposal()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        string app = Path.Combine(_root, "ClashSharp.exe");
        string icon = Path.Combine(_root, "Assets", "icon.bin");
        File.WriteAllBytes(app, [1, 2, 3]);
        File.WriteAllBytes(icon, [4, 5, 6]);
        using WindowsMaintenancePackageStateLease lease = WindowsMaintenancePackageStateLease.Acquire(_root, CancellationToken.None);

        lease.Reverify(CancellationToken.None);
        Assert.Throws<IOException>(() => File.WriteAllBytes(app, [7, 8, 9]));
        Assert.Throws<IOException>(() => File.Delete(icon));
        Assert.Throws<IOException>(() => Directory.Move(_root, _root + ".moved"));

        lease.Dispose();
        File.WriteAllBytes(app, [7, 8, 9]);
        File.Delete(icon);
        Directory.Move(_root, _root + ".moved");
        Directory.Move(_root + ".moved", _root);
        Assert.Throws<ObjectDisposedException>(() => lease.Reverify(CancellationToken.None));
    }

    [Fact]
    public void CanonicalDigestDoesNotDependOnEnumerationOrderOrAbsoluteRoot()
    {
        string first = Path.Combine(_root, "first");
        string second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllBytes(Path.Combine(first, "b.bin"), [4, 5]);
        File.WriteAllBytes(Path.Combine(first, "a.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(second, "a.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(second, "b.bin"), [4, 5]);

        var reader = new WindowsMaintenancePackageStateReader();
        Assert.Equal(reader.Read(first, CancellationToken.None), reader.Read(second, CancellationToken.None));
        using var writable = new FileStream(Path.Combine(first, "a.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("name")]
    [InlineData("empty-directory")]
    [InlineData("empty-file")]
    public void OriginalDigestIncludesBytesNamesAndEmptyTreeEntries(string change)
    {
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "app.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        var reader = new WindowsMaintenancePackageStateReader();
        string original = reader.Read(_root, CancellationToken.None);
        if (change == "bytes") { File.WriteAllBytes(file, [7, 8, 9]); }
        else if (change == "name") { File.Move(file, Path.Combine(_root, "renamed.bin")); }
        else if (change == "empty-directory") { Directory.CreateDirectory(Path.Combine(_root, "extra")); }
        else { File.WriteAllBytes(Path.Combine(_root, "extra.bin"), []); }

        Assert.NotEqual(original, reader.Read(_root, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddedEntriesCannotPassRetainedTreeVerification(bool directory)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "app.bin"), [1, 2, 3]);
        using WindowsMaintenancePackageStateLease lease = WindowsMaintenancePackageStateLease.Acquire(_root, CancellationToken.None);
        if (directory) { Directory.CreateDirectory(Path.Combine(_root, "extra")); }
        else { File.WriteAllBytes(Path.Combine(_root, "extra.bin"), []); }

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() => lease.Reverify(CancellationToken.None));

        Assert.Equal("installer.recovery.original_package_contents_changed", failure.DiagnosticCode);
    }

    [Fact]
    public void EmptyPackageCannotBecomeAnOriginalInstallation()
    {
        Directory.CreateDirectory(_root);

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMaintenancePackageStateLease.Acquire(_root, CancellationToken.None));

        Assert.Equal("installer.recovery.original_package_contents_empty", failure.DiagnosticCode);
        Directory.Move(_root, _root + ".moved");
        Directory.Move(_root + ".moved", _root);
    }

    [Fact]
    public void OversizedFileRejectsBeforeHashingAndReleasesItsHandle()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "huge.bin");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.SetLength(InstallerPayloadBudgets.MaximumFileBytes + 1);
        }

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMaintenancePackageStateLease.Acquire(_root, CancellationToken.None));

        Assert.Equal("installer.recovery.original_package_contents_byte_budget_invalid", failure.DiagnosticCode);
        using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void TooManyEntriesRejectsAndReleasesAllEarlierHandles()
    {
        Directory.CreateDirectory(_root);
        for (int index = 0; index <= InstallerPayloadBudgets.MaximumPackageArchiveEntries; index++)
        {
            File.WriteAllBytes(Path.Combine(_root, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin"), []);
        }

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMaintenancePackageStateLease.Acquire(_root, CancellationToken.None));

        Assert.Equal("installer.recovery.original_package_contents_entry_budget_invalid", failure.DiagnosticCode);
        Directory.Move(_root, _root + ".moved");
        Directory.Move(_root + ".moved", _root);
    }

    [Fact]
    public void PrecancelledReadDoesNotOpenOriginalPackage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => WindowsMaintenancePackageStateLease.Acquire(_root, cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
