using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferPhaseExecutorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CoordinatesAllSixRealStepsAndLeavesTheOrdinaryContinuationIntact(bool previous, bool next)
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture(previous, next);
        var persistence = new PrivatePersistence();
        var store = new InstallerOwnerTransferStore(persistence);
        var prepared = await store.SaveAsync(fixture.Journal, null, CancellationToken.None);
        fixture.Native.BeforeWrite = path =>
        {
            Assert.False(fixture.Backend.Present);
            Assert.True(fixture.Native.Entries.ContainsKey(WindowsOwnerTransferAccessFixture.ContinuationPath));
            if (path.StartsWith(WindowsOwnerTransferAccessFixture.Installer, StringComparison.Ordinal))
            {
                Assert.Equal(new InstallerOwnerTransferCertificateState(
                    fixture.Journal.NextCertificateLedger, fixture.Journal.PreviousCertificateLedger, null), fixture.State.Certificates.State);
            }
        };

        InstallerTransactionSnapshot continuation = await new InstallerOwnerTransferCoordinator(store, fixture.Executor)
            .ResumeAsync(prepared, CancellationToken.None);

        Assert.Equal(InstallerTransactionSnapshot.Create(fixture.Journal.Continuation), continuation);
        Assert.Equal(continuation, await fixture.Ordinary.LoadAsync(CancellationToken.None));
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.Equal(Enum.GetValues<InstallerOwnerTransferPhase>(), persistence.Phases);
        Assert.Equal("ordinary-prepared", fixture.Mutations[0]);
        Assert.Equal("service-removed", fixture.Mutations[1]);
        Assert.Equal("association-transferred", fixture.Mutations[2]);
        Assert.Equal(1, persistence.Deletes);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.State.Machine.Release.Disposed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ResumesEveryLostPhaseSaveAndTheVerifiedClearBoundary(int cut)
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture();
        var persistence = new PrivatePersistence { FailAt = cut };
        var store = new InstallerOwnerTransferStore(persistence);
        var prepared = await store.SaveAsync(fixture.Journal, null, CancellationToken.None);

        await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
            new InstallerOwnerTransferCoordinator(store, fixture.Executor).ResumeAsync(prepared, CancellationToken.None));

        Assert.Equal(0, fixture.Native.LiveLeases);
        var durable = await store.LoadAsync(CancellationToken.None);
        Assert.NotNull(durable);
        Assert.Equal((InstallerOwnerTransferPhase)(cut - 1), durable.Journal.Phase);
        persistence.FailAt = null;
        // Recreate the coordinator/store to exercise durable replay independently of their instance state.
        var resumedStore = new InstallerOwnerTransferStore(persistence);
        var continuation = await new InstallerOwnerTransferCoordinator(resumedStore, fixture.Executor)
            .ResumeAsync(durable, CancellationToken.None);

        Assert.Equal(InstallerTransactionSnapshot.Create(fixture.Journal.Continuation), continuation);
        Assert.Null(await resumedStore.LoadAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Ordinary.Saves);
        Assert.Equal(1, fixture.Mutations.Count(value => value == "service-removed"));
        Assert.Equal(1, fixture.Mutations.Count(value => value == "association-transferred"));
        Assert.Equal(fixture.Native.Writes.Count, fixture.Native.Writes.Distinct().Count());
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.Prepared)]
    [InlineData(InstallerOwnerTransferPhase.PreviousServiceRemoved)]
    [InlineData(InstallerOwnerTransferPhase.Verified)]
    [InlineData((InstallerOwnerTransferPhase)100)]
    public async Task CannotSkipARequiredMutationBoundary(InstallerOwnerTransferPhase next)
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture();

        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Executor.ApplyAndVerifyAsync(fixture.Journal, next, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.executor_phase_invalid", error.DiagnosticCode);
        Assert.Empty(fixture.Native.Opened);
        Assert.Empty(fixture.Mutations);
    }

    private sealed class PrivatePersistence : IInstallerOwnerTransferPersistence
    {
        private byte[]? _bytes;
        internal int? FailAt { get; set; }
        internal List<InstallerOwnerTransferPhase> Phases { get; } = [];
        internal int Deletes { get; private set; }

        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_bytes?.ToArray());
        }
        public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallerOwnerTransferJournal journal = InstallerOwnerTransferCodec.Parse(bytes.Span);
            if ((int)journal.Phase == FailAt)
            {
                throw new IOException("Synthetic private save interruption.");
            }
            _bytes = bytes.ToArray();
            Phases.Add(journal.Phase);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailAt == 8)
            {
                throw new IOException("Synthetic private clear interruption.");
            }
            Assert.NotNull(_bytes);
            Assert.Equal(InstallerOwnerTransferPhase.Verified, InstallerOwnerTransferCodec.Parse(_bytes).Phase);
            _bytes = null;
            Deletes++;
            return Task.CompletedTask;
        }
    }
}
