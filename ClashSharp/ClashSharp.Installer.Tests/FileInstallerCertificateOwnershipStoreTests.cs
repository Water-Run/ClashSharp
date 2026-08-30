using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class FileInstallerCertificateOwnershipStoreTests
{
    [Fact]
    public async Task StoreRoundTripsEveryAllowedTransitionAndClearsOnlyUnreferencedIdentity()
    {
        using TemporaryDirectory directory = new();
        RecordingRootGuard guard = new();
        using FileInstallerCertificateOwnershipStore store = new(directory.Path, guard);
        InstallerCertificateOwnershipLedger preExisting = InstallerTestData.CertificateLedger(
            wasPreExisting: true);

        InstallerCertificateOwnershipSnapshot first = await store.SaveAsync(
            preExisting,
            expectedCurrentHash: null,
            CancellationToken.None);
        InstallerCertificateOwnershipSnapshot loaded =
            Assert.IsType<InstallerCertificateOwnershipSnapshot>(
                await store.LoadAsync(CancellationToken.None));
        InstallerCertificateOwnershipSnapshot owned = await store.SaveAsync(
            loaded.Ledger.TakeOwnershipForMissingCertificate(),
            loaded.ContentHash,
            CancellationToken.None);
        InstallerCertificateOwnershipSnapshot unreferenced = await store.SaveAsync(
            owned.Ledger.PrepareRemoval(),
            owned.ContentHash,
            CancellationToken.None);

        Assert.Equal(first, loaded);
        Assert.True(owned.Ledger.InstallerOwned);
        Assert.Equal(0, unreferenced.Ledger.ManagedReferenceCount);
        Assert.NotEqual(first.ContentHash, owned.ContentHash);
        Assert.NotEqual(owned.ContentHash, unreferenced.ContentHash);
        Assert.True(guard.CallCount >= 4);

        await store.ClearUnreferencedAsync(
            unreferenced.Ledger.LedgerId,
            unreferenced.ContentHash,
            CancellationToken.None);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RewritingTheExactCurrentGenerationIsIdempotent()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerCertificateOwnershipStore store = new(
            directory.Path,
            new RecordingRootGuard());
        InstallerCertificateOwnershipSnapshot first = await store.SaveAsync(
            InstallerTestData.CertificateLedger(),
            expectedCurrentHash: null,
            CancellationToken.None);

        InstallerCertificateOwnershipSnapshot second = await store.SaveAsync(
            first.Ledger,
            first.ContentHash,
            CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task StaleCompareAndSwapHashIsRejected()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerCertificateOwnershipStore store = new(
            directory.Path,
            new RecordingRootGuard());
        InstallerCertificateOwnershipSnapshot current = await store.SaveAsync(
            InstallerTestData.CertificateLedger(),
            expectedCurrentHash: null,
            CancellationToken.None);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.SaveAsync(
                current.Ledger.PrepareRemoval(),
                InstallerTestData.OtherHash,
                CancellationToken.None));

        Assert.Equal("installer.certificate.write_conflict", exception.DiagnosticCode);
    }

    [Fact]
    public async Task NonNullExpectedHashCannotCreateANewLedger()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerCertificateOwnershipStore store = new(
            directory.Path,
            new RecordingRootGuard());

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.SaveAsync(
                InstallerTestData.CertificateLedger(),
                InstallerTestData.Hash,
                CancellationToken.None));

        Assert.Equal("installer.certificate.write_conflict", exception.DiagnosticCode);
        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorrectHashCannotChangeIdentityOrSkipAGeneration()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerCertificateOwnershipStore store = new(
            directory.Path,
            new RecordingRootGuard());
        InstallerCertificateOwnershipSnapshot current = await store.SaveAsync(
            InstallerTestData.CertificateLedger(wasPreExisting: true),
            expectedCurrentHash: null,
            CancellationToken.None);
        InstallerCertificateOwnershipLedger changedIdentity = current.Ledger
            .PrepareRemoval() with
        {
            CertificateSha256 = InstallerTestData.OtherHash,
        };
        InstallerCertificateOwnershipLedger skipped = current.Ledger
            .TakeOwnershipForMissingCertificate()
            .PrepareRemoval();

        foreach (InstallerCertificateOwnershipLedger invalidSuccessor in
                 new[] { changedIdentity, skipped })
        {
            InstallerProtocolException exception =
                await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                    store.SaveAsync(
                        invalidSuccessor,
                        current.ContentHash,
                        CancellationToken.None));
            Assert.Equal("installer.certificate.write_conflict", exception.DiagnosticCode);
        }

        Assert.Equal(
            current,
            await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClearRequiresZeroReferencesExactIdAndExactHash()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerCertificateOwnershipStore store = new(
            directory.Path,
            new RecordingRootGuard());
        InstallerCertificateOwnershipSnapshot active = await store.SaveAsync(
            InstallerTestData.CertificateLedger(),
            expectedCurrentHash: null,
            CancellationToken.None);
        InstallerProtocolException activeFailure =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                store.ClearUnreferencedAsync(
                    active.Ledger.LedgerId,
                    active.ContentHash,
                    CancellationToken.None));
        Assert.Equal("installer.certificate.clear_conflict", activeFailure.DiagnosticCode);

        InstallerCertificateOwnershipSnapshot unreferenced = await store.SaveAsync(
            active.Ledger.PrepareRemoval(),
            active.ContentHash,
            CancellationToken.None);
        foreach ((string ledgerId, string hash) in new[]
                 {
                     (InstallerTestData.OtherHash, unreferenced.ContentHash),
                     (unreferenced.Ledger.LedgerId, InstallerTestData.OtherHash),
                 })
        {
            InstallerProtocolException mismatch =
                await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                    store.ClearUnreferencedAsync(ledgerId, hash, CancellationToken.None));
            Assert.Equal("installer.certificate.clear_conflict", mismatch.DiagnosticCode);
        }

        Assert.NotNull(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExistingDirectoryAtLedgerPathIsRejectedInsteadOfTreatedAsMissing()
    {
        using TemporaryDirectory directory = new();
        Directory.CreateDirectory(System.IO.Path.Combine(
            directory.Path,
            FileInstallerCertificateOwnershipStore.LedgerFileName));
        using FileInstallerCertificateOwnershipStore store = new(
            directory.Path,
            new RecordingRootGuard());

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => store.LoadAsync(CancellationToken.None));

        Assert.Equal("installer.certificate.target_not_file", exception.DiagnosticCode);
    }

    [Fact]
    public async Task MalformedAndOversizedLedgerFilesFailClosed()
    {
        using TemporaryDirectory directory = new();
        string ledgerPath = System.IO.Path.Combine(
            directory.Path,
            FileInstallerCertificateOwnershipStore.LedgerFileName);
        using FileInstallerCertificateOwnershipStore store = new(
            directory.Path,
            new RecordingRootGuard());

        await File.WriteAllTextAsync(ledgerPath, "{}", CancellationToken.None);
        InstallerProtocolException malformed = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => store.LoadAsync(CancellationToken.None));
        Assert.Equal("installer.certificate.json_invalid", malformed.DiagnosticCode);

        await File.WriteAllBytesAsync(
            ledgerPath,
            new byte[InstallerCertificateOwnershipCodec.MaximumDocumentBytes + 1],
            CancellationToken.None);
        InstallerProtocolException oversized = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => store.LoadAsync(CancellationToken.None));
        Assert.Equal("installer.certificate.size_invalid", oversized.DiagnosticCode);
    }

    [Fact]
    public void RelativeRootIsRejectedWithoutTouchingFilesystem()
    {
        Assert.Throws<ArgumentException>(() =>
            new FileInstallerCertificateOwnershipStore("relative", new RecordingRootGuard()));
    }

    [Fact]
    public async Task DisposeIsIdempotentAndSubsequentUseIsRejected()
    {
        using TemporaryDirectory directory = new();
        FileInstallerCertificateOwnershipStore store = new(
            directory.Path,
            new RecordingRootGuard());

        store.Dispose();
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.LoadAsync(CancellationToken.None));
    }

    private sealed class RecordingRootGuard : IInstallerTransactionRootGuard
    {
        internal int CallCount { get; private set; }

        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Path.IsPathFullyQualified(absoluteRootPath));
            Assert.True(Directory.Exists(absoluteRootPath));
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ClashSharp.Installer.Certificate.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
