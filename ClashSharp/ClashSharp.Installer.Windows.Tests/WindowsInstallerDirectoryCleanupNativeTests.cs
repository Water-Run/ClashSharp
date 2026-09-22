using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>Requires an isolated elevated Windows runner; never uses actual product directories.</summary>
[Trait("Category", "IsolatedNativeDirectoryCleanup")]
public sealed class WindowsInstallerDirectoryCleanupNativeTests
{
    [Fact]
    public async Task NativeCreationLedgerAndHandleDeletionCompleteAllSixRoles()
    {
        using var fixture = new Fixture();
        fixture.CreateAllOwned();
        WindowsInstallerDirectoryLedger? recorded = await fixture.Ledger.LoadAsync(CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.Equal(6, recorded.Directories.Count);

        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();

        Assert.All(report.Entries, entry => Assert.Equal(InstallerDirectoryCleanupDisposition.Deleted, entry.Disposition));
        Assert.All(Enum.GetValues<InstallerDirectoryRole>(), role => Assert.False(Directory.Exists(fixture.Layout.GetPath(role))));
        Assert.Null(await fixture.Ledger.LoadAsync(CancellationToken.None));
        Assert.False(File.Exists(fixture.Layout.LedgerPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingOrReplacedDirectoriesAreNeverAdopted(bool replaceAfterCreation)
    {
        using var fixture = new Fixture();
        string product = fixture.Layout.GetPath(InstallerDirectoryRole.ProgramFilesProduct);
        if (!replaceAfterCreation) { new DirectoryInfo(product).Create(fixture.Security); }
        fixture.CreateAllOwned();
        if (replaceAfterCreation)
        {
            Directory.Move(product, fixture.ReplacedDirectory);
            new DirectoryInfo(product).Create(fixture.Security);
        }

        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();

        Assert.Equal(InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership, report.Entries[0].Disposition);
        Assert.True(Directory.Exists(product));
        Assert.True(report.HasRetained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryChildrenAndDirectoryDataStreamsAreRetained(bool namedStream)
    {
        using var fixture = new Fixture();
        fixture.CreateAllOwned();
        string product = fixture.Layout.GetPath(InstallerDirectoryRole.ProgramFilesProduct);
        string file = namedStream ? product + ":retained-test-data" : Path.Combine(product, "retained-test-data.txt");
        fixture.RetainedFile = file;
        File.WriteAllText(file, "fixture-owned user data");

        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();

        Assert.Equal(InstallerDirectoryCleanupDisposition.RetainedNonEmpty, report.Entries[0].Disposition);
        Assert.Equal("fixture-owned user data", File.ReadAllText(file));
        Assert.True(Directory.Exists(product));
    }

    [Fact]
    public async Task UntrustedLedgerIsRejectedWithoutReplacingItOrCreatingProductDirectories()
    {
        using var fixture = new Fixture();
        // Inherit the fixture's owner-readable directory policy rather than the exact public
        // ledger-file policy. Even a syntactically valid foreign file must not be adopted.
        byte[] foreign = WindowsInstallerDirectoryLedgerCodec.Serialize(WindowsInstallerDirectoryLedger.Empty);
        File.WriteAllBytes(fixture.Layout.LedgerPath, foreign);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Ledger.LoadAsync(CancellationToken.None));
        Assert.Throws<InstallerProtocolException>(() => fixture.CreateAllOwned());

        Assert.Equal(foreign, File.ReadAllBytes(fixture.Layout.LedgerPath));
        Assert.False(Directory.Exists(fixture.Layout.GetPath(InstallerDirectoryRole.ProgramFilesProduct)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _programFiles;
        private readonly string _programData;
        internal DirectorySecurity Security { get; }
        internal WindowsInstallerDirectoryCleanupLayout Layout { get; }
        internal WindowsInstallerDirectoryLedgerPersistence Ledger { get; }
        internal string ReplacedDirectory => Path.Combine(_programFiles, "replaced-test-directory");
        internal string? RetainedFile { get; set; }

        internal Fixture()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
                "Run native cleanup tests in an isolated elevated Windows runner.");
            string sid = identity.User!.Value;
            Security = WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(sid);
            _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ClashSharp.DirectoryCleanup.Tests." + Guid.NewGuid().ToString("N"));
            _programFiles = Path.Combine(_root, "ProgramFiles");
            _programData = Path.Combine(_root, "ProgramData");
            Layout = new(_programFiles, _programData);
            Ledger = new(Layout, () => WindowsInstallerDirectoryAnchor.Acquire(_programData),
                WindowsInstallerDirectoryLedgerFileNative.Instance);
            try
            {
                new DirectoryInfo(_root).Create(Security);
                new DirectoryInfo(_programFiles).Create(Security);
                new DirectoryInfo(_programData).Create(Security);
            }
            catch { Dispose(); throw; }
        }

        internal void CreateAllOwned()
        {
            var creation = new WindowsInstallerOwnedDirectoryCreation(Layout, Ledger, WindowsInstallerOwnedDirectoryCreationNative.Instance);
            foreach (InstallerDirectoryRole role in Enum.GetValues<InstallerDirectoryRole>())
            {
                creation.Create(Layout.GetPath(role), Security);
            }
        }

        internal Task<InstallerDirectoryCleanupReport> CompleteAsync()
        {
            InstallerTransactionSnapshot verified = InstallerTransactionSnapshot.Create(new(InstallerTransactionJournal.CurrentSchema,
                new string('a', 64), InstallerOperation.Uninstall, "S-1-5-21-100-200-300-1001", false,
                "1.0.0.0", new string('b', 64), InstallerTransactionPhase.Verified, 5));
            return new WindowsInstallerEmptyDirectoryFinalizer(Layout, Ledger, WindowsInstallerEmptyDirectoryNative.Instance)
                .CompleteAsync(verified, () => ValueTask.CompletedTask, CancellationToken.None);
        }

        public void Dispose()
        {
            // Delete only the exact test-created files and empty directories. Unexpected children
            // make cleanup fail rather than being removed recursively.
            if (RetainedFile is not null) { File.Delete(RetainedFile); }
            File.Delete(Layout.LedgerPath);
            foreach (InstallerDirectoryRole role in WindowsInstallerDirectoryCleanupLayout.DeletionOrder)
            {
                DeleteEmpty(Layout.GetPath(role));
            }
            DeleteEmpty(ReplacedDirectory);
            DeleteEmpty(_programFiles);
            DeleteEmpty(_programData);
            DeleteEmpty(_root);
        }

        private static void DeleteEmpty(string path)
        {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: false); }
        }
    }
}
