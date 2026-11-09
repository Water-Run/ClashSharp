using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Ownership contract of directory creation: a directory enters the cleanup ledger only when this very
/// call observed its creation, never because it already existed or because an earlier interrupted call
/// created it, and every failure along the create-observe-record sequence leaves a recoverable state.
/// </summary>
public sealed class WindowsInstallerOwnedDirectoryCreationContractTests
{
    private static readonly WindowsInstallerDirectoryCleanupLayout Layout = new(@"C:\Program Files", @"C:\ProgramData");

    public static TheoryData<InstallerDirectoryRole> Roles => [.. Enum.GetValues<InstallerDirectoryRole>()];

    public static TheoryData<string> FailureStages => ["load", "create", "open", "save"];

    [Theory]
    [MemberData(nameof(Roles))]
    public void CreationIsRecordedWithTheIdentityObservedForThatCallAndItsLeaseIsReleased(InstallerDirectoryRole role)
    {
        var fixture = new Fixture();

        fixture.Creation.Create(Layout.GetPath(role), new DirectorySecurity());

        WindowsInstallerOwnedDirectory entry = Assert.Single(fixture.Ledger.Current!.Directories);
        Assert.Equal(role, entry.Role);
        Assert.Equal(fixture.Native.IdentityOf(Layout.GetPath(role)), entry.Identity);
        Assert.Equal(1, fixture.Ledger.Current.Generation);
        Assert.Equal(1, fixture.Ledger.SaveCount);
        Assert.Equal(1, fixture.Native.Created);
        Assert.Equal(1, fixture.Native.Opened);
        Assert.Equal(0, fixture.Native.OpenLeases);
    }

    [Fact]
    public void AllRolesAccumulateInRoleOrderWithOneGenerationPerObservedCreation()
    {
        var fixture = new Fixture();
        InstallerDirectoryRole[] roles = Enum.GetValues<InstallerDirectoryRole>();

        foreach (InstallerDirectoryRole role in roles.Reverse())
        {
            fixture.Creation.Create(Layout.GetPath(role), new DirectorySecurity());
        }

        WindowsInstallerDirectoryLedger ledger = fixture.Ledger.Current!;
        Assert.Equal(roles, ledger.Directories.Select(static entry => entry.Role));
        Assert.Equal(roles.Length, ledger.Generation);
        Assert.Equal(
            roles.Length,
            ledger.Directories.Select(static entry => entry.Identity).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void AnAlreadyExistingDirectoryIsNeverAdoptedIntoTheLedger(InstallerDirectoryRole role)
    {
        var fixture = new Fixture();
        string path = Layout.GetPath(role);
        fixture.Native.Preexisting(path);

        fixture.Creation.Create(path, new DirectorySecurity());

        Assert.Null(fixture.Ledger.Current);
        Assert.Equal(0, fixture.Ledger.SaveCount);
        Assert.Equal(0, fixture.Native.Opened);
        Assert.Equal(1, fixture.Native.CreateCalls);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void PathCaseDoesNotChangeWhichRoleIsRecorded(InstallerDirectoryRole role)
    {
        var fixture = new Fixture();

        fixture.Creation.Create(Layout.GetPath(role).ToUpperInvariant(), new DirectorySecurity());

        Assert.Equal(role, Assert.Single(fixture.Ledger.Current!.Directories).Role);
    }

    [Fact]
    public void RecreatedDirectoryReplacesItsRoleEntryWithTheNewIdentity()
    {
        var fixture = new Fixture();
        string path = Layout.GetPath(InstallerDirectoryRole.AuthorityVersion);
        fixture.Creation.Create(path, new DirectorySecurity());
        WindowsFileIdentity first = fixture.Ledger.Current!.Directories.Single().Identity;
        fixture.Native.Remove(path);

        fixture.Creation.Create(path, new DirectorySecurity());

        WindowsInstallerOwnedDirectory entry = Assert.Single(fixture.Ledger.Current.Directories);
        Assert.NotEqual(first, entry.Identity);
        Assert.Equal(fixture.Native.IdentityOf(path), entry.Identity);
        Assert.Equal(2, fixture.Ledger.Current.Generation);
    }

    [Theory]
    [InlineData(@"C:\Program Files\ClashSharp\Service")]
    [InlineData(@"C:\ProgramData\ClashSharp\MihomoService")]
    [InlineData(@"C:\Program Files\ClashSharp\")]
    [InlineData(@"C:\Program Files\ClashSharp\Other")]
    [InlineData(@"C:\ProgramData\ClashSharp\Installer\v3")]
    [InlineData(@"C:\ProgramData\ClashSharp\InstallerAuthority\v1\child")]
    [InlineData(@"D:\Isolated\Test Root")]
    [InlineData(@"C:\Program Files\ClashSharp\..\ClashSharp")]
    public void PathsOutsideTheFixedLayoutAreCreatedButNeverLedgered(string path)
    {
        var fixture = new Fixture();

        fixture.Creation.Create(path, new DirectorySecurity());

        Assert.Equal(1, fixture.Native.CreateCalls);
        Assert.Equal(0, fixture.Native.Opened);
        Assert.Null(fixture.Ledger.Current);
        Assert.Equal(0, fixture.Ledger.SaveCount);
    }

    [Fact]
    public void ForeignLedgerStateRejectsBeforeAnyDirectoryIsCreated()
    {
        var fixture = new Fixture();
        var failure = new InstallerProtocolException("installer.directory_ledger.document_invalid");
        fixture.Ledger.BeforeRead = _ => throw failure;

        InstallerProtocolException actual = Assert.Throws<InstallerProtocolException>(() =>
            fixture.Creation.Create(Layout.GetPath(InstallerDirectoryRole.ProgramFilesProduct), new DirectorySecurity()));

        Assert.Same(failure, actual);
        Assert.Equal(0, fixture.Native.CreateCalls);
    }

    [Theory]
    [MemberData(nameof(FailureStages))]
    public void FailureAtAnyStageNeverLeavesAnAdoptableUnrecordedDirectory(string stage)
    {
        var fixture = new Fixture();
        string path = Layout.GetPath(InstallerDirectoryRole.InstallerRoot);
        var injected = new IOException("injected " + stage);
        fixture.Fail(stage, injected);

        IOException actual = Assert.Throws<IOException>(() => fixture.Creation.Create(path, new DirectorySecurity()));

        Assert.Same(injected, actual);
        Assert.Equal(0, fixture.Native.OpenLeases);
        Assert.Null(fixture.Ledger.Current);
        bool directoryWasCreated = stage is "open" or "save";
        Assert.Equal(directoryWasCreated, fixture.Native.Exists(path));

        // A retry after the interruption sees a directory it did not create in that call. It must
        // never claim it; only a failure before creation allows the retry to create and record it.
        fixture.Fail(stage, null);
        fixture.Creation.Create(path, new DirectorySecurity());

        if (directoryWasCreated)
        {
            Assert.Null(fixture.Ledger.Current);
            Assert.Equal(0, fixture.Ledger.SaveCount);
        }
        else
        {
            Assert.Equal(InstallerDirectoryRole.InstallerRoot, Assert.Single(fixture.Ledger.Current!.Directories).Role);
        }
    }

    [Fact]
    public void RepeatingTheSameObservedIdentityIsIdempotentForTheLedgerContent()
    {
        var fixture = new Fixture();
        string path = Layout.GetPath(InstallerDirectoryRole.ProgramDataProduct);
        fixture.Creation.Create(path, new DirectorySecurity());
        byte[] first = WindowsInstallerDirectoryLedgerCodec.Serialize(fixture.Ledger.Current!);
        fixture.Native.ForceReportCreated(path);

        fixture.Creation.Create(path, new DirectorySecurity());

        Assert.Equal(first, WindowsInstallerDirectoryLedgerCodec.Serialize(fixture.Ledger.Current!));
    }

    [Fact]
    public void ConstructorAndArgumentsAreValidated()
    {
        var fixture = new Fixture();
        Assert.Throws<ArgumentNullException>(() => new WindowsInstallerOwnedDirectoryCreation(null!, fixture.Ledger, fixture.Native));
        Assert.Throws<ArgumentNullException>(() => new WindowsInstallerOwnedDirectoryCreation(Layout, null!, fixture.Native));
        Assert.Throws<ArgumentNullException>(() => new WindowsInstallerOwnedDirectoryCreation(Layout, fixture.Ledger, null!));
        Assert.Throws<ArgumentNullException>(() =>
            fixture.Creation.Create(Layout.GetPath(InstallerDirectoryRole.ProgramFilesProduct), null!));
    }

    private sealed class Fixture
    {
        private readonly FaultyLedger _faulty;

        internal Fixture()
        {
            Ledger = new DirectoryCleanupTestLedger();
            _faulty = new FaultyLedger(Ledger);
            Native = new CreationNative();
            Creation = new WindowsInstallerOwnedDirectoryCreation(Layout, _faulty, Native);
        }

        internal DirectoryCleanupTestLedger Ledger { get; }

        internal CreationNative Native { get; }

        internal WindowsInstallerOwnedDirectoryCreation Creation { get; }

        internal void Fail(string stage, Exception? failure)
        {
            switch (stage)
            {
                case "load":
                    Ledger.BeforeRead = failure is null ? null : _ => throw failure;
                    break;
                case "create":
                    Native.CreateFailure = failure;
                    break;
                case "open":
                    Native.OpenFailure = failure;
                    break;
                default:
                    _faulty.SaveFailure = failure;
                    break;
            }
        }
    }

    private sealed class FaultyLedger(IWindowsInstallerDirectoryLedgerPersistence inner) : IWindowsInstallerDirectoryLedgerPersistence
    {
        internal Exception? SaveFailure { get; set; }

        public Task<WindowsInstallerDirectoryLedger?> LoadAsync(CancellationToken cancellationToken) =>
            inner.LoadAsync(cancellationToken);

        public Task SaveAsync(
            WindowsInstallerDirectoryLedger? expected,
            WindowsInstallerDirectoryLedger desired,
            CancellationToken cancellationToken) =>
            SaveFailure is null ? inner.SaveAsync(expected, desired, cancellationToken) : Task.FromException(SaveFailure);

        public Task DeleteAsync(WindowsInstallerDirectoryLedger expected, CancellationToken cancellationToken) =>
            inner.DeleteAsync(expected, cancellationToken);
    }

    private sealed class CreationNative : IWindowsInstallerOwnedDirectoryCreationNative
    {
        private readonly Dictionary<string, WindowsFileIdentity> _directories = new(StringComparer.OrdinalIgnoreCase);
        private ulong _next = 100;
        private string? _forced;

        internal Exception? CreateFailure { get; set; }

        internal Exception? OpenFailure { get; set; }

        internal int CreateCalls { get; private set; }

        internal int Created { get; private set; }

        internal int Opened { get; private set; }

        internal int OpenLeases { get; private set; }

        internal WindowsFileIdentity IdentityOf(string path) => _directories[path];

        internal bool Exists(string path) => _directories.ContainsKey(path);

        internal void Preexisting(string path) => _directories[path] = new WindowsFileIdentity(7, _next++);

        internal void Remove(string path) => _directories.Remove(path);

        internal void ForceReportCreated(string path) => _forced = path;

        public bool CreateDirectory(string path, DirectorySecurity security)
        {
            CreateCalls++;
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }

            if (string.Equals(_forced, path, StringComparison.OrdinalIgnoreCase))
            {
                _forced = null;
                Created++;
                return true;
            }

            if (_directories.ContainsKey(path))
            {
                return false;
            }

            _directories[path] = new WindowsFileIdentity(7, _next++);
            Created++;
            return true;
        }

        public IWindowsInstallerCreatedDirectoryLease OpenCreatedDirectory(string path)
        {
            Opened++;
            if (OpenFailure is not null)
            {
                throw OpenFailure;
            }

            OpenLeases++;
            return new Lease(this, _directories[path]);
        }

        private sealed class Lease(CreationNative owner, WindowsFileIdentity identity) : IWindowsInstallerCreatedDirectoryLease
        {
            private bool _disposed;

            public WindowsFileIdentity Identity => identity;

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    owner.OpenLeases--;
                }
            }
        }
    }
}
