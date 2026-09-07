using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Certificates;
using ClashSharp.Installer.Windows.Transactions;
using static ClashSharp.Installer.Windows.Tests.WindowsArchivedCertificateRemovalAdapterTests;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerArchivedCertificatePersistenceTests
{
    private const string PrivateRoot = @"C:\ProgramData\ClashSharp\InstallerAuthority\v1";

    [Fact]
    public async Task LazyPersistenceRevalidatesThePrivateRootAndTouchesOnlyTheRetiredArchive()
    {
        var events = new List<string>();
        var guard = new Guard(events);
        var files = new Files(events);
        using var persistence = WindowsInstallerArchivedCertificatePersistence.CreateForTesting(
            PrivateRoot, RetiredSid, guard, () => true, files);
        Assert.Empty(events);

        await persistence.ReadAsync(CancellationToken.None);
        await persistence.WriteAtomicallyAsync(new byte[] { 1 }, CancellationToken.None);
        await persistence.DeleteAsync(CancellationToken.None);

        Assert.Equal(["guard", "read", "guard", "write", "guard", "delete"], events);
        Assert.All(files.Paths, path => Assert.Equal(Path.Combine(PrivateRoot,
            WindowsInstallerCertificateArchiveLayout.GetFileName(RetiredSid)), path));
        persistence.Dispose();
        Assert.True(guard.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => persistence.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingPrivateRootIsReadAsAbsenceWithoutOpeningFilesAndCannotBeCreatedByMutation()
    {
        var events = new List<string>();
        using var persistence = WindowsInstallerArchivedCertificatePersistence.CreateForTesting(
            PrivateRoot, RetiredSid, new Guard(events), () => false, new Files(events));

        Assert.Null(await persistence.ReadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            persistence.WriteAtomicallyAsync(new byte[] { 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => persistence.DeleteAsync(CancellationToken.None));

        Assert.Equal(["guard", "guard", "guard"], events);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    public async Task ChangedRootPermissionsBlockAllArchiveIo(string operation)
    {
        var events = new List<string>();
        var guard = new Guard(events) { Reject = true };
        using var persistence = WindowsInstallerArchivedCertificatePersistence.CreateForTesting(
            PrivateRoot, RetiredSid, guard, () => true, new Files(events));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => InvokeAsync(persistence, operation, CancellationToken.None));
        Assert.Equal(["guard"], events);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("transfer")]
    [InlineData("other-sid")]
    [InlineData("temporary")]
    [InlineData("relative")]
    [InlineData("normalized")]
    [InlineData("alternate-stream")]
    public async Task ArchiveNativeRejectsOtherLeavesBeforeFilesystemAccess(string condition)
    {
        string ownPath = Path.Combine(PrivateRoot, WindowsInstallerCertificateArchiveLayout.GetFileName(RetiredSid));
        string path = condition switch
        {
            "active" => Path.Combine(PrivateRoot, FileInstallerCertificateOwnershipStore.LedgerFileName),
            "transfer" => Path.Combine(PrivateRoot, InstallerOwnerTransferStateLayout.JournalFileName),
            "other-sid" => Path.Combine(PrivateRoot, WindowsInstallerCertificateArchiveLayout.GetFileName("S-1-5-21-100-200-300-1002")),
            "temporary" => ownPath + ".tmp",
            "relative" => Path.GetFileName(ownPath),
            "normalized" => Path.Combine(PrivateRoot, "..", "v1", Path.GetFileName(ownPath)),
            "alternate-stream" => ownPath + ":extra",
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        var native = WindowsInstallerPrivateJournalFileNative.CreateForCertificateArchive(RetiredSid);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => native.ReadAsync(path, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => native.WriteAtomicallyAsync(path, new byte[] { 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => native.DeleteAsync(path, CancellationToken.None));
        Assert.Throws<InstallerProtocolException>(() => native.IsPresent(path, CancellationToken.None));
    }

    [Fact]
    public async Task TransferJournalNativeStillRejectsTheArchiveNamespace()
    {
        string archive = Path.Combine(PrivateRoot, WindowsInstallerCertificateArchiveLayout.GetFileName(RetiredSid));
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            WindowsInstallerPrivateJournalFileNative.Instance.ReadAsync(archive, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task ArchiveNativeEnforcesTheCertificateBoundBeforeOpeningTheFixedPath(int length)
    {
        string archive = Path.Combine(PrivateRoot, WindowsInstallerCertificateArchiveLayout.GetFileName(RetiredSid));
        var native = WindowsInstallerPrivateJournalFileNative.CreateForCertificateArchive(RetiredSid);
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            native.WriteAtomicallyAsync(archive, new byte[length], CancellationToken.None));
        Assert.Equal("installer.certificate_archive.document_size_invalid", error.DiagnosticCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("S-1-5-21-100-200-300-01001")]
    [InlineData("S-1-5-21-100-200-300-1001\\Root")]
    public void ArchiveLayoutRejectsUntrustedSidForms(string targetSid) =>
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerCertificateArchiveLayout.GetFileName(targetSid));

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    public async Task PreCancellationPerformsNoGuardOrFileCalls(string operation)
    {
        var events = new List<string>();
        using var persistence = WindowsInstallerArchivedCertificatePersistence.CreateForTesting(
            PrivateRoot, RetiredSid, new Guard(events), () => true, new Files(events));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(persistence, operation, cancellation.Token));
        Assert.Empty(events);
    }

    private static Task InvokeAsync(WindowsInstallerArchivedCertificatePersistence persistence, string operation,
        CancellationToken cancellationToken) => operation switch
        {
            "read" => persistence.ReadAsync(cancellationToken),
            "write" => persistence.WriteAtomicallyAsync(new byte[] { 1 }, cancellationToken),
            "delete" => persistence.DeleteAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private sealed class Guard(List<string> events) : IInstallerTransactionRootGuard, IDisposable
    {
        internal bool Reject { get; set; }
        internal bool Disposed { get; private set; }
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(PrivateRoot, absoluteRootPath);
            events.Add("guard");
            if (Reject)
            {
                throw new InstallerProtocolException("fixture.root_changed");
            }
            return Task.CompletedTask;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Files(List<string> events) : IWindowsInstallerPrivateJournalFileNative
    {
        internal List<string> Paths { get; } = [];
        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            events.Add("read");
            return Task.FromResult<byte[]?>(null);
        }
        public Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            events.Add("write");
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            events.Add("delete");
            return Task.CompletedTask;
        }
    }
}
