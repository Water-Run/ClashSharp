using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerOwnerTransferStoreTests
{
    [Fact]
    public async Task CreateAdvanceAndReplayObserveTheSameDurableEvidenceWithoutRewritingReplay()
    {
        var persistence = new FakePersistence();
        var store = new InstallerOwnerTransferStore(persistence);
        Assert.Equal(0, persistence.ReadCalls);
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.CreateJournal();

        InstallerOwnerTransferSnapshot created = await store.SaveAsync(journal, null, CancellationToken.None);
        InstallerOwnerTransferSnapshot advanced = await store.SaveAsync(
            journal.TransitionTo(InstallerOwnerTransferPhase.StartupBlocked), created.ContentHash, CancellationToken.None);
        InstallerOwnerTransferSnapshot replay = await store.SaveAsync(advanced.Journal, advanced.ContentHash, CancellationToken.None);
        InstallerOwnerTransferSnapshot? reloaded = await new InstallerOwnerTransferStore(persistence).LoadAsync(CancellationToken.None);

        Assert.Equal(advanced, replay);
        Assert.Equal(advanced, reloaded);
        Assert.Equal(2, persistence.WriteCalls);
        Assert.Equal(0, persistence.DeleteCalls);
        AssertClearedBuffers(persistence);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LostOrCancelledWriteAcknowledgementsAreResolvedFromPersistedState(bool applyWrite, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var persistence = new FakePersistence
        {
            ApplyMutation = applyWrite,
            MutationFailure = cancel ? null : new IOException("private transport detail"),
            OnMutation = cancel ? cancellation.Cancel : null,
        };
        var store = new InstallerOwnerTransferStore(persistence);
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.CreateJournal();

        if (applyWrite)
        {
            InstallerOwnerTransferSnapshot result = await store.SaveAsync(journal, null, cancellation.Token);
            Assert.Equal(journal, result.Journal);
        }
        else if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(journal, null, cancellation.Token));
        }
        else
        {
            InstallerStateUncertainException exception = await Assert.ThrowsAsync<InstallerStateUncertainException>(
                () => store.SaveAsync(journal, null, cancellation.Token));
            Assert.Equal("installer.owner_transfer.write_state_uncertain", exception.DiagnosticCode);
            Assert.Null(exception.InnerException);
        }

        Assert.Equal(1, persistence.WriteCalls);
        Assert.Equal(0, persistence.DeleteCalls);
        Assert.False(persistence.ReadTokens[^1].CanBeCanceled);
        Assert.Equal(applyWrite, persistence.Bytes is not null);
        AssertClearedBuffers(persistence);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LostOrCancelledDeleteAcknowledgementsPreserveOrConfirmVerifiedEvidence(bool applyDelete, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.AtPhase(InstallerOwnerTransferPhase.Verified);
        InstallerOwnerTransferSnapshot snapshot = InstallerOwnerTransferSnapshot.Create(journal);
        var persistence = new FakePersistence
        {
            Bytes = InstallerOwnerTransferCodec.Serialize(journal),
            ApplyMutation = applyDelete,
            MutationFailure = cancel ? null : new IOException("private transport detail"),
            OnMutation = cancel ? cancellation.Cancel : null,
        };
        var store = new InstallerOwnerTransferStore(persistence);

        if (applyDelete)
        {
            await store.ClearVerifiedAsync(journal.Continuation.TransactionId, snapshot.ContentHash, cancellation.Token);
        }
        else if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ClearVerifiedAsync(journal.Continuation.TransactionId, snapshot.ContentHash, cancellation.Token));
        }
        else
        {
            InstallerStateUncertainException exception = await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
                store.ClearVerifiedAsync(journal.Continuation.TransactionId, snapshot.ContentHash, cancellation.Token));
            Assert.Equal("installer.owner_transfer.clear_state_uncertain", exception.DiagnosticCode);
            Assert.Null(exception.InnerException);
        }

        Assert.Equal(0, persistence.WriteCalls);
        Assert.Equal(1, persistence.DeleteCalls);
        Assert.False(persistence.ReadTokens[^1].CanBeCanceled);
        Assert.Equal(!applyDelete, persistence.Bytes is not null);
        AssertClearedBuffers(persistence);
    }

    [Fact]
    public async Task StaleSaveAndPrematureClearNeverReachMutationPorts()
    {
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.CreateJournal();
        InstallerOwnerTransferSnapshot current = InstallerOwnerTransferSnapshot.Create(journal);
        var persistence = new FakePersistence { Bytes = InstallerOwnerTransferCodec.Serialize(journal) };
        var store = new InstallerOwnerTransferStore(persistence);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.SaveAsync(
            journal.TransitionTo(InstallerOwnerTransferPhase.StartupBlocked), InstallerTestData.OtherHash, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.ClearVerifiedAsync(
            journal.Continuation.TransactionId, current.ContentHash, CancellationToken.None));

        Assert.Equal(0, persistence.WriteCalls);
        Assert.Equal(0, persistence.DeleteCalls);
        Assert.Equal(journal, InstallerOwnerTransferCodec.Parse(persistence.Bytes!));
        AssertClearedBuffers(persistence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableStateAfterMutationIsUncertainWithoutAnotherMutation(bool delete)
    {
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.AtPhase(
            delete ? InstallerOwnerTransferPhase.Verified : InstallerOwnerTransferPhase.Prepared);
        InstallerOwnerTransferSnapshot snapshot = InstallerOwnerTransferSnapshot.Create(journal);
        var persistence = new FakePersistence
        {
            Bytes = delete ? InstallerOwnerTransferCodec.Serialize(journal) : null,
            FailReadAfterMutation = true,
        };
        var store = new InstallerOwnerTransferStore(persistence);

        InstallerStateUncertainException exception = await Assert.ThrowsAsync<InstallerStateUncertainException>(async () =>
        {
            if (delete)
            {
                await store.ClearVerifiedAsync(journal.Continuation.TransactionId, snapshot.ContentHash, CancellationToken.None);
            }
            else
            {
                await store.SaveAsync(journal, null, CancellationToken.None);
            }
        });

        Assert.Equal(delete ? "installer.owner_transfer.clear_state_uncertain" : "installer.owner_transfer.write_state_uncertain", exception.DiagnosticCode);
        Assert.Equal(1, persistence.WriteCalls + persistence.DeleteCalls);
        Assert.Null(exception.InnerException);
        AssertClearedBuffers(persistence);
    }

    [Fact]
    public async Task MalformedExistingBytesArePreservedAndNeverOverwritten()
    {
        byte[] original = [0x01, 0x02];
        var persistence = new FakePersistence { Bytes = original };
        var store = new InstallerOwnerTransferStore(persistence);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.SaveAsync(
            InstallerOwnerTransferJournalTests.CreateJournal(), null, CancellationToken.None));

        Assert.Equal(original, persistence.Bytes);
        Assert.Equal(0, persistence.WriteCalls);
        AssertClearedBuffers(persistence);
    }

    [Fact]
    public async Task PreCancellationDoesNotReadOrMutatePrivateState()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var persistence = new FakePersistence();
        var store = new InstallerOwnerTransferStore(persistence);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            InstallerOwnerTransferJournalTests.CreateJournal(), null, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ClearVerifiedAsync(
            InstallerTestData.TransactionId, InstallerTestData.Hash, cancellation.Token));

        Assert.Equal(0, persistence.ReadCalls);
        Assert.Equal(0, persistence.WriteCalls);
        Assert.Equal(0, persistence.DeleteCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidClearIdentityDoesNotOpenPrivatePersistence(bool invalidTransaction)
    {
        var persistence = new FakePersistence();
        var store = new InstallerOwnerTransferStore(persistence);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.ClearVerifiedAsync(
            invalidTransaction ? "invalid" : InstallerTestData.TransactionId,
            invalidTransaction ? InstallerTestData.Hash : "invalid",
            CancellationToken.None));

        Assert.Equal(0, persistence.ReadCalls);
        Assert.Equal(0, persistence.DeleteCalls);
    }

    [Fact]
    public async Task CancellationAfterReadReturnsStillClearsTheOwnedPrivateBuffer()
    {
        using var cancellation = new CancellationTokenSource();
        var persistence = new FakePersistence
        {
            Bytes = InstallerOwnerTransferCodec.Serialize(InstallerOwnerTransferJournalTests.CreateJournal()),
            BeforeRead = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
        };
        var store = new InstallerOwnerTransferStore(persistence);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));

        Assert.Single(persistence.ReturnedBuffers);
        AssertClearedBuffers(persistence);
        Assert.Equal(0, persistence.WriteCalls + persistence.DeleteCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnexpectedPostMutationEvidenceIsPreservedWithoutRetry(bool delete, bool malformed)
    {
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.AtPhase(
            delete ? InstallerOwnerTransferPhase.Verified : InstallerOwnerTransferPhase.Prepared);
        InstallerOwnerTransferSnapshot snapshot = InstallerOwnerTransferSnapshot.Create(journal);
        InstallerOwnerTransferJournal other = InstallerOwnerTransferJournalTests.CreateJournal();
        byte[] unexpected = malformed ? [0x01] : InstallerOwnerTransferCodec.Serialize(
            other with { Continuation = other.Continuation with { TransactionId = InstallerTestData.OtherHash } });
        var persistence = new FakePersistence
        {
            Bytes = delete ? InstallerOwnerTransferCodec.Serialize(journal) : null,
        };
        persistence.OnMutation = () => persistence.Bytes = unexpected;
        var store = new InstallerOwnerTransferStore(persistence);

        InstallerStateUncertainException exception = await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
            delete
                ? store.ClearVerifiedAsync(journal.Continuation.TransactionId, snapshot.ContentHash, CancellationToken.None)
                : store.SaveAsync(journal, null, CancellationToken.None));

        Assert.Equal(delete ? "installer.owner_transfer.clear_state_uncertain" : "installer.owner_transfer.write_state_uncertain", exception.DiagnosticCode);
        Assert.Null(exception.InnerException);
        Assert.Same(unexpected, persistence.Bytes);
        Assert.Equal(1, persistence.WriteCalls + persistence.DeleteCalls);
        AssertClearedBuffers(persistence);
    }

    [Fact]
    public async Task ConcurrentCallIsRejectedAndGuardIsReleasedAfterTheOwnedReadCompletes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new FakePersistence
        {
            BeforeRead = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            },
        };
        var store = new InstallerOwnerTransferStore(persistence);
        Task<InstallerOwnerTransferSnapshot?> pending = store.LoadAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                store.SaveAsync(InstallerOwnerTransferJournalTests.CreateJournal(), null, CancellationToken.None));
            Assert.Equal("installer.owner_transfer.concurrent_access", exception.DiagnosticCode);
        }
        finally
        {
            release.TrySetResult();
            await pending;
        }

        persistence.BeforeRead = null;
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.Equal(2, persistence.ReadCalls);
    }

    private static void AssertClearedBuffers(FakePersistence persistence)
    {
        Assert.All(persistence.ReturnedBuffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.All(persistence.LastWriteMemory.ToArray(), value => Assert.Equal(0, value));
    }

    private sealed class FakePersistence : IInstallerOwnerTransferPersistence
    {
        internal byte[]? Bytes { get; set; }
        internal bool ApplyMutation { get; init; } = true;
        internal Exception? MutationFailure { get; init; }
        internal Action? OnMutation { get; set; }
        internal bool FailReadAfterMutation { get; init; }
        internal Func<CancellationToken, Task>? BeforeRead { get; set; }
        internal List<byte[]> ReturnedBuffers { get; } = [];
        internal List<CancellationToken> ReadTokens { get; } = [];
        internal ReadOnlyMemory<byte> LastWriteMemory { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        internal int DeleteCalls { get; private set; }

        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            ReadTokens.Add(cancellationToken);
            if (BeforeRead is not null)
            {
                await BeforeRead(cancellationToken);
            }

            if (FailReadAfterMutation && WriteCalls + DeleteCalls > 0)
            {
                throw new IOException("private path detail");
            }

            byte[]? copy = Bytes?.ToArray();
            if (copy is not null)
            {
                ReturnedBuffers.Add(copy);
            }

            return copy;
        }

        public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            WriteCalls++;
            LastWriteMemory = bytes;
            if (ApplyMutation)
            {
                Bytes = bytes.ToArray();
            }

            AfterMutation(cancellationToken);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (ApplyMutation)
            {
                Bytes = null;
            }

            AfterMutation(cancellationToken);
            return Task.CompletedTask;
        }

        private void AfterMutation(CancellationToken cancellationToken)
        {
            OnMutation?.Invoke();
            if (MutationFailure is not null)
            {
                throw MutationFailure;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
