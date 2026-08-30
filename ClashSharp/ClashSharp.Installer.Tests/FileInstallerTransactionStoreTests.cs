using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class FileInstallerTransactionStoreTests
{
    [Fact]
    public async Task StoreRoundTripsGenerationsAndClearsOnlyVerifiedIdentity()
    {
        using TemporaryDirectory directory = new();
        RecordingRootGuard guard = new();
        using FileInstallerTransactionStore store = new(directory.Path, guard);
        InstallerTransactionJournal prepared = InstallerTestData.Journal();

        InstallerTransactionSnapshot first = await store.SaveAsync(
            prepared,
            expectedCurrentHash: null,
            CancellationToken.None);
        InstallerTransactionSnapshot loaded = Assert.IsType<InstallerTransactionSnapshot>(
            await store.LoadAsync(CancellationToken.None));
        InstallerTransactionJournal reserved = loaded.Journal.TransitionTo(
            InstallerTransactionPhase.MachineReserved);
        InstallerTransactionSnapshot second = await store.SaveAsync(
            reserved,
            loaded.ContentHash,
            CancellationToken.None);
        InstallerTransactionJournal package = second.Journal.TransitionTo(
            InstallerTransactionPhase.PackageCommitted);
        InstallerTransactionSnapshot third = await store.SaveAsync(
            package,
            second.ContentHash,
            CancellationToken.None);

        Assert.Equal(first.Journal, prepared);
        Assert.Equal(reserved, second.Journal);
        Assert.Equal(package, third.Journal);
        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.True(guard.CallCount >= 3);

        InstallerTransactionSnapshot machine = await store.SaveAsync(
            package.TransitionTo(InstallerTransactionPhase.MachineCommitted),
            third.ContentHash,
            CancellationToken.None);
        InstallerTransactionSnapshot verified = await store.SaveAsync(
            machine.Journal.TransitionTo(InstallerTransactionPhase.Verified),
            machine.ContentHash,
            CancellationToken.None);
        await store.ClearVerifiedAsync(
            verified.Journal.TransactionId,
            verified.ContentHash,
            CancellationToken.None);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StaleCompareAndSwapHashIsRejected()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerTransactionStore store = new(directory.Path, new RecordingRootGuard());
        InstallerTransactionSnapshot current = await store.SaveAsync(
            InstallerTestData.Journal(),
            expectedCurrentHash: null,
            CancellationToken.None);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.SaveAsync(
                current.Journal.TransitionTo(InstallerTransactionPhase.MachineReserved),
                InstallerTestData.OtherHash,
                CancellationToken.None));
        Assert.Equal("installer.transaction.write_conflict", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ClearRejectsANonverifiedJournal()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerTransactionStore store = new(directory.Path, new RecordingRootGuard());
        InstallerTransactionSnapshot current = await store.SaveAsync(
            InstallerTestData.Journal(),
            expectedCurrentHash: null,
            CancellationToken.None);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.ClearVerifiedAsync(
                current.Journal.TransactionId,
                current.ContentHash,
                CancellationToken.None));
        Assert.Equal("installer.transaction.clear_conflict", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ExistingDirectoryAtJournalPathIsRejectedInsteadOfTreatedAsMissing()
    {
        using TemporaryDirectory directory = new();
        Directory.CreateDirectory(System.IO.Path.Combine(
            directory.Path,
            FileInstallerTransactionStore.JournalFileName));
        using FileInstallerTransactionStore store = new(directory.Path, new RecordingRootGuard());

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => store.LoadAsync(CancellationToken.None));

        Assert.Equal("installer.transaction.target_not_file", exception.DiagnosticCode);
    }

    [Fact]
    public async Task MalformedAndOversizedJournalFilesFailClosed()
    {
        using TemporaryDirectory directory = new();
        string journalPath = System.IO.Path.Combine(
            directory.Path,
            FileInstallerTransactionStore.JournalFileName);
        using FileInstallerTransactionStore store = new(directory.Path, new RecordingRootGuard());

        await File.WriteAllTextAsync(journalPath, "{}", CancellationToken.None);
        InstallerProtocolException malformed = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => store.LoadAsync(CancellationToken.None));
        Assert.Equal("installer.transaction.json_invalid", malformed.DiagnosticCode);

        await File.WriteAllBytesAsync(
            journalPath,
            new byte[InstallerTransactionCodec.MaximumDocumentBytes + 1],
            CancellationToken.None);
        InstallerProtocolException oversized = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => store.LoadAsync(CancellationToken.None));
        Assert.Equal("installer.transaction.size_invalid", oversized.DiagnosticCode);
    }

    [Fact]
    public async Task CorrectHashCannotReplaceADifferentTransactionIdentity()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerTransactionStore store = new(directory.Path, new RecordingRootGuard());
        InstallerTransactionSnapshot current = await store.SaveAsync(
            InstallerTestData.Journal(),
            expectedCurrentHash: null,
            CancellationToken.None);
        InstallerTransactionJournal differentIdentity = current.Journal
            .TransitionTo(InstallerTransactionPhase.MachineReserved) with
        {
            TransactionId = InstallerTestData.OtherHash,
        };

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.SaveAsync(differentIdentity, current.ContentHash, CancellationToken.None));

        Assert.Equal("installer.transaction.write_conflict", exception.DiagnosticCode);
        InstallerTransactionSnapshot loaded = Assert.IsType<InstallerTransactionSnapshot>(
            await store.LoadAsync(CancellationToken.None));
        Assert.Equal(current, loaded);
    }

    [Fact]
    public async Task ClearRequiresBothExactTransactionIdAndExactContentHash()
    {
        using TemporaryDirectory directory = new();
        using FileInstallerTransactionStore store = new(directory.Path, new RecordingRootGuard());
        InstallerTransactionSnapshot current = await store.SaveAsync(
            InstallerTestData.Journal(),
            expectedCurrentHash: null,
            CancellationToken.None);
        foreach (InstallerTransactionPhase phase in
                 new[]
                 {
                     InstallerTransactionPhase.MachineReserved,
                     InstallerTransactionPhase.PackageCommitted,
                     InstallerTransactionPhase.MachineCommitted,
                     InstallerTransactionPhase.Verified,
                 })
        {
            current = await store.SaveAsync(
                current.Journal.TransitionTo(phase),
                current.ContentHash,
                CancellationToken.None);
        }

        foreach ((string transactionId, string contentHash) in new[]
                 {
                     (InstallerTestData.OtherHash, current.ContentHash),
                     (current.Journal.TransactionId, InstallerTestData.OtherHash),
                 })
        {
            InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                store.ClearVerifiedAsync(transactionId, contentHash, CancellationToken.None));
            Assert.Equal("installer.transaction.clear_conflict", exception.DiagnosticCode);
        }

        Assert.NotNull(await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public void RelativeRootIsRejectedWithoutTouchingFilesystem()
    {
        Assert.Throws<ArgumentException>(() =>
            new FileInstallerTransactionStore("relative", new RecordingRootGuard()));
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
                $"ClashSharp.Installer.Tests.{Guid.NewGuid():N}");
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
