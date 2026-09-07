using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Certificates;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineCertificatePersistenceTests
{
    private const string Root = @"C:\ProgramData\ClashSharp\InstallerAuthority\v1";

    [Fact]
    public async Task MissingRootReadsAndRemovalDoNotCreateDirectories()
    {
        var read = new Guard();
        var write = new Guard();
        var files = new Files();
        using var persistence = new WindowsMachineCertificatePersistence(Root, read, write, () => false, files);
        Assert.Equal(0, read.Calls);
        Assert.Null(await persistence.ReadAsync(CancellationToken.None));
        await persistence.DeleteAsync(CancellationToken.None);
        Assert.Equal(2, read.Calls);
        Assert.Equal(0, write.Calls);
        Assert.Empty(files.Paths);
    }

    [Fact]
    public async Task OnlyValidatedWritesAcquireCreationAuthorityAndAllOperationsUseTheFixedPrivateLeaf()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        byte[] document = InstallerMachineCertificateOwnership.Create(fixture.Manifest, false).Serialize();
        var read = new Guard();
        var write = new Guard();
        var files = new Files();
        using var persistence = new WindowsMachineCertificatePersistence(Root, read, write, () => true, files);
        await persistence.ReadAsync(CancellationToken.None);
        await persistence.WriteAtomicallyAsync(document, CancellationToken.None);
        await persistence.DeleteAsync(CancellationToken.None);
        Assert.Equal(2, read.Calls);
        Assert.Equal(1, write.Calls);
        Assert.Equal(3, files.Paths.Count);
        Assert.All(files.Paths, path => Assert.Equal(Path.Combine(Root, InstallerMachineCertificateOwnership.FileName), path));
        persistence.Dispose();
        Assert.True(read.Disposed);
        Assert.True(write.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => persistence.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidDocumentsDoNotCreatePrivateDirectories()
    {
        var read = new Guard();
        var write = new Guard();
        var files = new Files();
        using var persistence = new WindowsMachineCertificatePersistence(Root, read, write, () => false, files);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => persistence.WriteAtomicallyAsync(new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(0, write.Calls);
        Assert.Empty(files.Paths);
    }

    [Theory]
    [InlineData("certificate-ownership-v1.json")]
    [InlineData("owner-transfer-v1.json")]
    [InlineData("retired-uninstall-v1.json")]
    [InlineData("machine-certificate-ownership-v1.json.tmp")]
    [InlineData("machine-certificate-ownership-v1.json:stream")]
    public async Task MachineCertificateNativeCannotAccessOtherAuthorityDocuments(string name)
    {
        string path = Path.Combine(Root, name);
        var native = WindowsInstallerPrivateJournalFileNative.CreateForMachineCertificate();
        await Assert.ThrowsAsync<InstallerProtocolException>(() => native.ReadAsync(path, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => native.WriteAtomicallyAsync(path, new byte[] { 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => native.DeleteAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ExistingTransferCapabilityCannotAccessTheMachineCertificateDocument()
    {
        string path = Path.Combine(Root, InstallerMachineCertificateOwnership.FileName);
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            WindowsInstallerPrivateJournalFileNative.Instance.ReadAsync(path, CancellationToken.None));
    }

    private sealed class Guard : IInstallerTransactionRootGuard, IDisposable
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Root, absoluteRootPath);
            Calls++;
            return Task.CompletedTask;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Files : IWindowsInstallerPrivateJournalFileNative
    {
        internal List<string> Paths { get; } = [];
        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult<byte[]?>(null);
        }
        public Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.CompletedTask;
        }
    }
}
