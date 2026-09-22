using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerEmptyDirectoryFinalizerTests
{
    [Fact]
    public async Task DeletesOwnedEmptyDirectoriesAfterReleasingResourcesAndClearsTerminalLast()
    {
        var fixture = new Fixture();
        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();

        Assert.All(report.Entries, entry => Assert.Equal(InstallerDirectoryCleanupDisposition.Deleted, entry.Disposition));
        Assert.False(report.HasRetained);
        Assert.Empty(fixture.Native.Nodes);
        Assert.Null(fixture.Ledger.Current);
        Assert.Equal("release", fixture.Events[0]);
        Assert.Equal("ledger-delete", fixture.Events[^1]);
        Assert.Equal(0, fixture.Native.ActiveLeases);
        Assert.Equal(WindowsInstallerDirectoryCleanupLayout.DeletionOrder,
            fixture.Native.DeletedRoles);
    }

    [Theory]
    [InlineData("unrecorded", InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership)]
    [InlineData("replaced", InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership)]
    [InlineData("nonempty", InstallerDirectoryCleanupDisposition.RetainedNonEmpty)]
    public async Task PreservesUnownedReplacedAndNonemptyDirectories(string state, InstallerDirectoryCleanupDisposition expected)
    {
        var fixture = new Fixture();
        const InstallerDirectoryRole role = InstallerDirectoryRole.ProgramFilesProduct;
        DirectoryNode node = fixture.Native.Nodes[role];
        if (state == "unrecorded")
        {
            WindowsInstallerDirectoryLedger owned = fixture.Ledger.Current!;
            fixture.Ledger.Current = new(owned.Generation, owned.Directories.Where(entry => entry.Role != role), owned.Terminal);
        }
        else if (state == "replaced") { node.Identity = new(12, 999); }
        else { node.Empty = false; }

        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();

        Assert.Equal(expected, report.Entries.Single(entry => entry.Role == role).Disposition);
        Assert.True(report.HasRetained);
        Assert.Same(node, fixture.Native.Nodes[role]);
        Assert.DoesNotContain(role, fixture.Native.DeletedRoles);
        Assert.Null(fixture.Ledger.Current);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("open")]
    [InlineData("security")]
    [InlineData("delete")]
    [InlineData("observe-after-delete")]
    [InlineData("ledger-delete")]
    public async Task FailureCannotCompleteUninstallAndLeavesItsExactRecoveryIdentity(string cut)
    {
        var fixture = new Fixture();
        var failure = new IOException("Injected directory finalization failure.");
        if (cut == "release") { fixture.Release = () => throw failure; }
        else if (cut == "open") { fixture.Native.BeforeOpen = _ => throw failure; }
        else if (cut == "security") { fixture.Native.Nodes[InstallerDirectoryRole.ProgramFilesProduct].SecurityFailure = failure; }
        else if (cut == "delete") { fixture.Native.Nodes[InstallerDirectoryRole.ProgramFilesProduct].DeleteFailure = failure; }
        else if (cut == "observe-after-delete")
        {
            fixture.Native.BeforeOpen = role =>
            {
                if (fixture.Native.DeletedRoles.Contains(role)) { throw failure; }
            };
        }
        else { fixture.Ledger.BeforeDelete = () => throw failure; }

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.CompleteAsync());

        Assert.Equal(Fixture.Verified, fixture.Ledger.Current!.Terminal);
        Assert.Equal(0, fixture.Native.ActiveLeases);
        Assert.DoesNotContain("ledger-delete", fixture.Events);
    }

    [Fact]
    public async Task ReopeningAfterPartialCleanupFinishesRemainingRolesWithoutRecreatingDeletedDirectories()
    {
        var fixture = new Fixture();
        fixture.Native.BeforeOpen = role =>
        {
            if (role == InstallerDirectoryRole.InstallerVersion) { throw new IOException("Interrupted after ProgramFiles."); }
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.CompleteAsync());
        Assert.False(fixture.Native.Nodes.ContainsKey(InstallerDirectoryRole.ProgramFilesProduct));
        Assert.Equal(Fixture.Verified, fixture.Ledger.Current!.Terminal);

        fixture.Native.BeforeOpen = null;
        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();

        Assert.Equal(InstallerDirectoryCleanupDisposition.Missing, report.Entries[0].Disposition);
        Assert.All(report.Entries.Skip(1), entry => Assert.Equal(InstallerDirectoryCleanupDisposition.Deleted, entry.Disposition));
        Assert.Empty(fixture.Native.Nodes);
        Assert.Null(fixture.Ledger.Current);
        Assert.Equal(6, fixture.Native.DeletedRoles.Distinct().Count());
    }

    [Fact]
    public async Task CancellationAfterADirectoryDeletionKeepsTheCheckpointForAnotherHelper()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Native.AfterDelete = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CompleteAsync(cancellation.Token));
        Assert.Equal(Fixture.Verified, fixture.Ledger.Current!.Terminal);
        Assert.Equal(0, fixture.Native.ActiveLeases);

        fixture.Native.AfterDelete = null;
        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();
        Assert.Equal(InstallerDirectoryCleanupDisposition.Missing, report.Entries[0].Disposition);
        Assert.Null(fixture.Ledger.Current);
    }

    [Fact]
    public async Task LostDeletionReplyIsResolvedOnlyByObservedAbsence()
    {
        var fixture = new Fixture();
        fixture.Native.AfterDelete = _ => throw new IOException("Deletion committed before the reply was lost.");

        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();

        Assert.All(report.Entries, entry => Assert.Equal(InstallerDirectoryCleanupDisposition.Deleted, entry.Disposition));
        Assert.Null(fixture.Ledger.Current);
    }

    [Fact]
    public async Task MissingLedgerOnVerifiedReplayDoesNotInventDirectoryOwnership()
    {
        var fixture = new Fixture();
        fixture.Ledger.Current = null;

        InstallerDirectoryCleanupReport report = await fixture.CompleteAsync();

        Assert.Equal(1, fixture.Ledger.SaveCount);
        Assert.All(report.Entries, entry => Assert.Equal(InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership, entry.Disposition));
        Assert.Empty(fixture.Native.DeletedRoles);
        Assert.Null(fixture.Ledger.Current);
    }

    [Fact]
    public async Task DifferentTerminalCannotReleaseResourcesOrAuthorizeDeletion()
    {
        var fixture = new Fixture();
        InstallerTransactionSnapshot other = InstallerTransactionSnapshot.Create(
            Fixture.Verified.Journal with { TransactionId = new string('c', 64) });
        fixture.Ledger.Current = new(1, [], other);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.CompleteAsync());

        Assert.Equal("installer.directory_ledger.terminal_identity_mismatch", failure.DiagnosticCode);
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Native.DeletedRoles);
        Assert.Equal(other, fixture.Ledger.Current.Terminal);
    }

    private sealed class Fixture
    {
        internal static InstallerTransactionSnapshot Verified { get; } = InstallerTransactionSnapshot.Create(new(
            InstallerTransactionJournal.CurrentSchema, new string('a', 64), InstallerOperation.Uninstall,
            "S-1-5-21-100-200-300-1001", false, "1.0.0.0", new string('b', 64), InstallerTransactionPhase.Verified, 5));
        internal WindowsInstallerDirectoryCleanupLayout Layout { get; } = new(@"C:\Program Files", @"C:\ProgramData");
        internal List<string> Events { get; } = [];
        internal MemoryLedger Ledger { get; }
        internal DirectoryNative Native { get; }
        internal Func<ValueTask>? Release { get; set; }

        internal Fixture()
        {
            Ledger = new(Events)
            {
                Current = new(6, Enum.GetValues<InstallerDirectoryRole>()
                    .Select(role => new WindowsInstallerOwnedDirectory(role, new(12, (ulong)role + 1))), Verified),
            };
            Native = new(Layout, Events);
        }

        internal Task<InstallerDirectoryCleanupReport> CompleteAsync(CancellationToken cancellationToken = default) =>
            new WindowsInstallerEmptyDirectoryFinalizer(Layout, Ledger, Native).CompleteAsync(Verified, () =>
            {
                Assert.Equal(Verified, Ledger.Current!.Terminal);
                Events.Add("release");
                Native.ResourcesReleased = true;
                return Release?.Invoke() ?? ValueTask.CompletedTask;
            }, cancellationToken);
    }

    private sealed class MemoryLedger(List<string> events) : IWindowsInstallerDirectoryLedgerPersistence
    {
        internal WindowsInstallerDirectoryLedger? Current { get; set; }
        internal int SaveCount { get; private set; }
        internal Action? BeforeDelete { get; set; }

        public Task<WindowsInstallerDirectoryLedger?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task SaveAsync(WindowsInstallerDirectoryLedger? expected, WindowsInstallerDirectoryLedger desired, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(Current, expected);
            Current = desired;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(WindowsInstallerDirectoryLedger expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(Current, expected);
            BeforeDelete?.Invoke();
            events.Add("ledger-delete");
            Current = null;
            return Task.CompletedTask;
        }
    }

    private sealed class DirectoryNode(WindowsFileIdentity identity)
    {
        internal WindowsFileIdentity Identity { get; set; } = identity;
        internal bool Empty { get; set; } = true;
        internal Exception? SecurityFailure { get; set; }
        internal Exception? DeleteFailure { get; set; }
    }

    private sealed class DirectoryNative(WindowsInstallerDirectoryCleanupLayout layout, List<string> events)
        : IWindowsInstallerEmptyDirectoryNative
    {
        internal Dictionary<InstallerDirectoryRole, DirectoryNode> Nodes { get; } = Enum.GetValues<InstallerDirectoryRole>()
            .ToDictionary(role => role, role => new DirectoryNode(new(12, (ulong)role + 1)));
        internal List<InstallerDirectoryRole> DeletedRoles { get; } = [];
        internal Action<InstallerDirectoryRole>? BeforeOpen { get; set; }
        internal Action<InstallerDirectoryRole>? AfterDelete { get; set; }
        internal bool ResourcesReleased { get; set; }
        internal int ActiveLeases { get; private set; }

        public IWindowsInstallerDirectoryDeletionLease? Open(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(ResourcesReleased);
            Assert.True(layout.TryGetRole(path, out InstallerDirectoryRole role));
            BeforeOpen?.Invoke(role);
            if (!Nodes.TryGetValue(role, out DirectoryNode? node)) { return null; }
            ActiveLeases++;
            return new Lease(this, role, node, events);
        }

        private sealed class Lease(DirectoryNative owner, InstallerDirectoryRole role, DirectoryNode node, List<string> events)
            : IWindowsInstallerDirectoryDeletionLease
        {
            private bool _disposed;
            public WindowsFileIdentity Identity => node.Identity;
            public bool IsEmpty => node.Empty;
            public void VerifyOwnedSecurity()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (node.SecurityFailure is { } failure) { throw failure; }
            }
            public void DeleteEmpty()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Assert.True(node.Empty);
                if (node.DeleteFailure is { } failure) { throw failure; }
                Assert.True(owner.Nodes.Remove(role));
                owner.DeletedRoles.Add(role);
                events.Add("delete-" + role);
                owner.AfterDelete?.Invoke(role);
            }
            public void Dispose()
            {
                if (!_disposed) { _disposed = true; owner.ActiveLeases--; }
            }
        }
    }
}
