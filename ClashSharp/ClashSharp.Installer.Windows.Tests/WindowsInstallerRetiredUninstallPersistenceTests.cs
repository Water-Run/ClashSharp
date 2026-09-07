using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed partial class WindowsInstallerOwnerTransferPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredAdmissionChecksOnlyTheDedicatedLeafAndCreatesNothing(bool present)
    {
        FakeDirectories native = CompletePrivateChain();
        var files = new RetiredFiles { Present = present };
        var admission = WindowsInstallerRetiredUninstallAdmission.CreateForTesting(
            () => WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferForTesting(@"C:\ProgramData", native), files);
        if (present)
        {
            var failure = await Assert.ThrowsAsync<InstallerProtocolException>(() => admission.EnsureNoRetiredUninstallAsync(default));
            Assert.Equal("installer.retired_uninstall.pending", failure.DiagnosticCode);
        }
        else { await admission.EnsureNoRetiredUninstallAsync(default); }
        Assert.Equal(["presence"], files.Calls);
        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Fact]
    public async Task RetiredAdmissionRejectsUnsafePrivateAclAndSkipsMissingRoot()
    {
        var native = new FakeDirectories();
        var files = new RetiredFiles { Present = true };
        var admission = WindowsInstallerRetiredUninstallAdmission.CreateForTesting(
            () => WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferForTesting(@"C:\ProgramData", native), files);
        await admission.EnsureNoRetiredUninstallAsync(default);
        Assert.Empty(files.Calls);
        native.Observations[AuthorityRoot] = native.Observations[ProductRoot];
        await Assert.ThrowsAsync<InstallerProtocolException>(() => admission.EnsureNoRetiredUninstallAsync(default));
        Assert.Empty(files.Calls);
        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredPersistenceIsLazyAndCannotWriteWithoutAProvenRoot(bool rootPresent)
    {
        var calls = new List<string>();
        var guard = new RecordingGuard(calls);
        var files = new RetiredFiles();
        using var persistence = WindowsInstallerRetiredUninstallPersistence.CreateForTesting(PrivateRoot, guard, () => rootPresent, files);
        Assert.Empty(calls);
        Assert.Null(await persistence.ReadAsync(default));
        if (rootPresent)
        {
            await persistence.WriteAtomicallyAsync(new byte[] { 1 }, default);
            await persistence.DeleteAsync(default);
            Assert.Equal(["read", "write", "delete"], files.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<InstallerProtocolException>(() => persistence.WriteAtomicallyAsync(new byte[] { 1 }, default));
            await Assert.ThrowsAsync<InstallerProtocolException>(() => persistence.DeleteAsync(default));
            Assert.Empty(files.Calls);
        }
        Assert.Equal(["guard", "guard", "guard"], calls);
        persistence.Dispose();
        Assert.True(guard.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => persistence.ReadAsync(default));
    }

    [Theory]
    [InlineData("owner-transfer-v1.json")]
    [InlineData("retired-uninstall-v1.json:stream")]
    [InlineData("transaction.json")]
    public async Task RetiredNativeFactoryCannotOpenAnotherPrivateOrOrdinaryLeaf(string leaf)
    {
        var files = WindowsInstallerPrivateJournalFileNative.CreateForRetiredUninstall();
        string path = Path.Combine(PrivateRoot, leaf);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => files.ReadAsync(path, default));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => files.WriteAtomicallyAsync(path, new byte[] { 1 }, default));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => files.DeleteAsync(path, default));
        Assert.Throws<InstallerProtocolException>(() => files.IsPresent(path, default));
    }

    private sealed class RetiredFiles : IWindowsInstallerPrivateJournalFileNative, IWindowsInstallerPrivateJournalPresenceNative
    {
        internal bool Present;
        internal List<string> Calls { get; } = [];
        private void Hit(string path, string operation)
        {
            Assert.Equal(Path.Combine(PrivateRoot, InstallerRetiredUninstallStore.JournalFileName), path);
            Calls.Add(operation);
        }
        public bool IsPresent(string path, CancellationToken cancellationToken) { Hit(path, "presence"); return Present; }
        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken) { Hit(path, "read"); return Task.FromResult<byte[]?>(null); }
        public Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) { Hit(path, "write"); return Task.CompletedTask; }
        public Task DeleteAsync(string path, CancellationToken cancellationToken) { Hit(path, "delete"); return Task.CompletedTask; }
    }
}
