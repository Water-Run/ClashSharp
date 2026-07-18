using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Infrastructure.Recovery;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies versioned, hashed, atomic mutation journal persistence.</summary>
public sealed class FileMutationJournalStoreTests
{
    /// <summary>Verifies a flushed journal round-trips with its content hash.</summary>
    [Fact]
    public async Task SaveAndLoadAsync_ValidJournal_RoundTrips()
    {
        await WithTemporaryStoreAsync(async (store, _) =>
        {
            MutationJournal journal = CreateJournal(generation: 1);
            MutationJournalSnapshot saved = await store.SaveAsync(journal, null, CancellationToken.None);
            MutationJournalSnapshot? loaded = await store.LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(64, saved.ContentHash.Length);
            Assert.Equal(saved.ContentHash, loaded.ContentHash);
            Assert.Equivalent(journal, loaded.Journal, strict: true);
        });
    }

    /// <summary>Verifies a caller cannot overwrite a journal using a stale expected hash.</summary>
    [Fact]
    public async Task SaveAsync_StaleExpectedHash_RejectsWithoutChangingCurrentJournal()
    {
        await WithTemporaryStoreAsync(async (store, _) =>
        {
            MutationJournalSnapshot first = await store.SaveAsync(CreateJournal(1), null, CancellationToken.None);
            MutationJournalStoreException exception = await Assert.ThrowsAsync<MutationJournalStoreException>(
                () => store.SaveAsync(CreateJournal(2), new string('0', 64), CancellationToken.None));
            MutationJournalSnapshot? loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(MutationJournalStoreError.ConcurrencyConflict, exception.Error);
            Assert.NotNull(loaded);
            Assert.Equal(first.ContentHash, loaded.ContentHash);
            Assert.Equal(1, loaded.Journal.Generation);
        });
    }

    /// <summary>Verifies journal generations advance exactly one step at a time.</summary>
    [Fact]
    public async Task SaveAsync_SkippedGeneration_RejectsWithoutPromotion()
    {
        await WithTemporaryStoreAsync(async (store, _) =>
        {
            MutationJournalSnapshot first = await store.SaveAsync(CreateJournal(1), null, CancellationToken.None);
            MutationJournalStoreException exception = await Assert.ThrowsAsync<MutationJournalStoreException>(
                () => store.SaveAsync(CreateJournal(3), first.ContentHash, CancellationToken.None));

            Assert.Equal(MutationJournalStoreError.InvalidGeneration, exception.Error);
            Assert.Equal(1, (await store.LoadAsync(CancellationToken.None))!.Journal.Generation);
        });
    }

    /// <summary>Verifies every injected write cut point leaves exactly one valid authoritative generation.</summary>
    /// <param name="failureStage">Write stage that raises the injected failure.</param>
    /// <param name="expectedGeneration">Generation that must be authoritative after the failure.</param>
    [Theory]
    [InlineData((int)MutationJournalWriteStage.AfterTemporaryFlush, 1)]
    [InlineData((int)MutationJournalWriteStage.BeforePromotion, 1)]
    [InlineData((int)MutationJournalWriteStage.AfterPromotion, 2)]
    public async Task SaveAsync_InjectedWriteFailure_PreservesOldOrNewValidGeneration(
        int failureStageValue,
        long expectedGeneration)
    {
        MutationJournalWriteStage failureStage = (MutationJournalWriteStage)failureStageValue;
        await WithTemporaryStoreAsync(async (store, rootPath) =>
        {
            MutationJournalSnapshot first = await store.SaveAsync(CreateJournal(1), null, CancellationToken.None);
            FileMutationJournalStore failingStore = new(
                rootPath,
                stage =>
                {
                    if (stage == failureStage)
                    {
                        throw new IOException("injected promotion failure");
                    }
                });

            await Assert.ThrowsAsync<IOException>(
                () => failingStore.SaveAsync(CreateJournal(2), first.ContentHash, CancellationToken.None));

            MutationJournalSnapshot? loaded = await store.LoadAsync(CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(expectedGeneration, loaded.Journal.Generation);
            if (expectedGeneration == 1)
            {
                Assert.Equal(first.ContentHash, loaded.ContentHash);
            }
            else
            {
                Assert.NotEqual(first.ContentHash, loaded.ContentHash);
            }
        });
    }

    /// <summary>Verifies corrupt bytes are diagnosed rather than treated as an empty journal.</summary>
    [Fact]
    public async Task LoadAsync_CorruptEnvelope_ReturnsTypedCorruptionFailure()
    {
        await WithTemporaryStoreAsync(async (store, rootPath) =>
        {
            Directory.CreateDirectory(rootPath);
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, FileMutationJournalStore.JournalFileName),
                "{\"schemaVersion\":1,\"payload\":\"truncated\"}");

            MutationJournalStoreException exception = await Assert.ThrowsAsync<MutationJournalStoreException>(
                () => store.LoadAsync(CancellationToken.None));

            Assert.Equal(MutationJournalStoreError.Corrupt, exception.Error);
        });
    }

    /// <summary>Verifies a recovery root containing a reparse point is rejected.</summary>
    [Fact]
    public void RecoveryRootPolicy_ReparsePoint_IsRejected()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), $"ClashSharp-Recovery-{Guid.NewGuid():N}");

        MutationJournalStoreException exception = Assert.Throws<MutationJournalStoreException>(
            () => RecoveryRootPolicy.ValidateNoReparsePoints(rootPath, _ => FileAttributes.ReparsePoint));

        Assert.Equal(MutationJournalStoreError.UnsafePath, exception.Error);
    }

    /// <summary>Verifies callers cannot create a recovery store from a relative root.</summary>
    [Fact]
    public void Constructor_RelativeRecoveryRoot_IsRejected()
    {
        MutationJournalStoreException exception = Assert.Throws<MutationJournalStoreException>(
            () => new FileMutationJournalStore("relative-recovery-root"));

        Assert.Equal(MutationJournalStoreError.UnsafePath, exception.Error);
    }

    /// <summary>Verifies cleanup requires the operation identifier and latest content hash.</summary>
    [Fact]
    public async Task DeleteAsync_MatchingIdentityAndHash_RemovesJournal()
    {
        await WithTemporaryStoreAsync(async (store, _) =>
        {
            MutationJournal journal = CreateJournal(1);
            MutationJournalSnapshot saved = await store.SaveAsync(journal, null, CancellationToken.None);

            await store.DeleteAsync(journal.OperationId, saved.ContentHash, CancellationToken.None);

            Assert.Null(await store.LoadAsync(CancellationToken.None));
        });
    }

    private static MutationJournal CreateJournal(long generation)
    {
        return new MutationJournal(
            MutationJournal.CurrentSchemaVersion,
            Guid.Parse("c21f4709-e766-4f77-8b0c-a0a40a6dfe1a"),
            "network-mode",
            generation,
            MutationJournalPhase.Applying,
            "baseline-hash",
            "desired-hash",
            HasCommitMarker: false,
            [new MutationJournalStep("network", MutationJournalPhase.Applying, IntentRecorded: true, Completed: false, "restore-baseline")]);
    }

    private static async Task WithTemporaryStoreAsync(Func<FileMutationJournalStore, string, Task> action)
    {
        string rootPath = Path.Combine(Path.GetTempPath(), $"ClashSharp-Recovery-{Guid.NewGuid():N}");
        try
        {
            await action(new FileMutationJournalStore(rootPath), rootPath);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
