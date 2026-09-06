using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerOwnerTransferCoordinatorTests
{
    [Theory]
    [MemberData(nameof(InstallerOwnerTransferJournalTests.AllPhases), MemberType = typeof(InstallerOwnerTransferJournalTests))]
    public async Task EveryDurablePhaseResumesToTheSameStillPendingOrdinaryContinuation(InstallerOwnerTransferPhase initial)
    {
        using Fixture fixture = await Fixture.CreateAsync(initial);
        InstallerOwnerTransferSnapshot expected = fixture.Snapshot;
        var coordinator = new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor);

        InstallerTransactionSnapshot continuation = await coordinator.ResumeAsync(expected, CancellationToken.None);

        Assert.Equal(await fixture.Ordinary.LoadAsync(CancellationToken.None), continuation);
        Assert.Equal(expected.Journal.Continuation, continuation.Journal);
        Assert.Equal(InstallerTransactionPhase.Prepared, continuation.Journal.Phase);
        Assert.False(continuation.Journal.AllowReassociation);
        Assert.Null(await fixture.Store.LoadAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Executor.VerificationCalls);
        Assert.Equal(1, fixture.Persistence.DeleteCalls);
        Assert.Equal(Enum.GetValues<InstallerOwnerTransferPhase>().Where(phase => phase > initial
            && phase < InstallerOwnerTransferPhase.Verified), fixture.Executor.Calls);
        Assert.Equal(Enum.GetValues<InstallerOwnerTransferPhase>().Where(phase => phase > initial), fixture.Persistence.SavedPhases);
        string ordinaryJson = Encoding.UTF8.GetString(InstallerTransactionCodec.Serialize(continuation.Journal));
        Assert.DoesNotContain("authenticationToken", ordinaryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("profileRoot", ordinaryJson, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MutationFailureCases))]
    public async Task InterruptedNativeStepPreservesThePriorPhaseAndReplaysWithoutDuplicatingItsMutation(
        InstallerOwnerTransferPhase failedPhase, bool afterMutation)
    {
        using Fixture fixture = await Fixture.CreateAsync(InstallerOwnerTransferPhase.Prepared);
        fixture.Executor.FailurePhase = failedPhase;
        fixture.Executor.FailAfterMutation = afterMutation;

        await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
            new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor).ResumeAsync(fixture.Snapshot, CancellationToken.None));

        InstallerOwnerTransferSnapshot pending = fixture.Snapshot;
        Assert.Equal((InstallerOwnerTransferPhase)((int)failedPhase - 1), pending.Journal.Phase);
        Assert.Equal(afterMutation, fixture.Executor.Applied.Contains(failedPhase));
        Assert.Equal(0, fixture.Persistence.DeleteCalls);
        Assert.DoesNotContain(fixture.Executor.Calls, phase => phase > failedPhase);

        fixture.Executor.FailurePhase = null;
        InstallerTransactionSnapshot result = await new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor)
            .ResumeAsync(pending, CancellationToken.None);

        Assert.Equal(result, await fixture.Ordinary.LoadAsync(CancellationToken.None));
        Assert.Null(await fixture.Store.LoadAsync(CancellationToken.None));
        Assert.Equal(2, fixture.Executor.Calls.Count(phase => phase == failedPhase));
        Assert.All(fixture.Executor.MutationCounts.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred, false)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred, true)]
    [InlineData(InstallerOwnerTransferPhase.Verified, false)]
    [InlineData(InstallerOwnerTransferPhase.Verified, true)]
    public async Task FailedFinalPostconditionsOrLostOrdinaryBarrierNeverClearPrivateEvidence(
        InstallerOwnerTransferPhase initial, bool removeBarrier)
    {
        using Fixture fixture = await Fixture.CreateAsync(initial);
        InstallerOwnerTransferSnapshot original = fixture.Snapshot;
        if (removeBarrier)
        {
            File.Delete(fixture.OrdinaryJournalPath);
        }
        else
        {
            fixture.Executor.FailVerification = true;
        }

        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor).ResumeAsync(original, CancellationToken.None));

        Assert.Equal(original, fixture.Snapshot);
        Assert.Empty(fixture.Executor.Calls);
        Assert.Equal(1, fixture.Executor.VerificationCalls);
        Assert.Empty(fixture.Persistence.SavedPhases);
        Assert.Equal(0, fixture.Persistence.DeleteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainFinalAcknowledgementRequiresVerificationAgainBeforeHandoff(bool clearing)
    {
        using Fixture fixture = await Fixture.CreateAsync(InstallerOwnerTransferPhase.InstallerAccessTransferred);
        fixture.Persistence.FailWritePhase = clearing ? null : InstallerOwnerTransferPhase.Verified;
        fixture.Persistence.FailDelete = clearing;

        await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
            new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor).ResumeAsync(fixture.Snapshot, CancellationToken.None));

        Assert.Equal(clearing ? InstallerOwnerTransferPhase.Verified : InstallerOwnerTransferPhase.InstallerAccessTransferred, fixture.Snapshot.Journal.Phase);
        Assert.Equal(1, fixture.Executor.VerificationCalls);
        Assert.NotNull(await fixture.Ordinary.LoadAsync(CancellationToken.None));
        fixture.Persistence.FailWritePhase = null;
        fixture.Persistence.FailDelete = false;

        InstallerTransactionSnapshot result = await new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor)
            .ResumeAsync(fixture.Snapshot, CancellationToken.None);

        Assert.Equal(2, fixture.Executor.VerificationCalls);
        Assert.Empty(fixture.Executor.Calls);
        Assert.Null(await fixture.Store.LoadAsync(CancellationToken.None));
        Assert.Equal(result, await fixture.Ordinary.LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("advanced")]
    [InlineData("other-identity")]
    public async Task ChangedOrMissingInitialEvidenceCannotAuthorizeAnyNativeStep(string condition)
    {
        using Fixture fixture = await Fixture.CreateAsync(InstallerOwnerTransferPhase.Prepared);
        InstallerOwnerTransferSnapshot expected = fixture.Snapshot;
        fixture.Persistence.Bytes = condition switch
        {
            "missing" => null,
            "advanced" => InstallerOwnerTransferCodec.Serialize(expected.Journal.TransitionTo(InstallerOwnerTransferPhase.StartupBlocked)),
            "other-identity" => InstallerOwnerTransferCodec.Serialize(expected.Journal with
            {
                Continuation = expected.Journal.Continuation with { TransactionId = InstallerTestData.OtherHash },
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor).ResumeAsync(expected, CancellationToken.None));

        Assert.Equal(condition == "missing" ? "installer.owner_transfer.state_missing" : "installer.owner_transfer.state_changed", failure.DiagnosticCode);
        Assert.Empty(fixture.Executor.Calls);
        Assert.Equal(0, fixture.Executor.VerificationCalls);
        Assert.Empty(fixture.Persistence.SavedPhases);
        Assert.Equal(0, fixture.Persistence.DeleteCalls);
    }

    [Fact]
    public async Task CancellationDrainsTheOwnedStepAndRejectsConcurrentExecutionUntilRecoveryCanResume()
    {
        using Fixture fixture = await Fixture.CreateAsync(InstallerOwnerTransferPhase.StartupBlocked);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        fixture.Executor.BeforeApply = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        InstallerOwnerTransferSnapshot original = fixture.Snapshot;
        var coordinator = new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor);
        Task<InstallerTransactionSnapshot> pending = coordinator.ResumeAsync(original, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                coordinator.ResumeAsync(original, CancellationToken.None));
            Assert.Equal("installer.owner_transfer.concurrent_execution", failure.DiagnosticCode);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        Assert.Equal(original, fixture.Snapshot);
        Assert.Contains(InstallerOwnerTransferPhase.PreviousServiceRemoved, fixture.Executor.Applied);
        Assert.Equal(0, fixture.Persistence.DeleteCalls);
        fixture.Executor.BeforeApply = null;

        await coordinator.ResumeAsync(fixture.Snapshot, CancellationToken.None);

        Assert.Equal(1, fixture.Executor.MutationCounts[InstallerOwnerTransferPhase.PreviousServiceRemoved]);
        Assert.Null(await fixture.Store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreCancellationDoesNotOpenTheStoreOrCallNativePorts()
    {
        using Fixture fixture = await Fixture.CreateAsync(InstallerOwnerTransferPhase.Prepared);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InstallerOwnerTransferCoordinator(fixture.Store, fixture.Executor).ResumeAsync(fixture.Snapshot, cancellation.Token));

        Assert.Equal(0, fixture.Persistence.ReadCalls);
        Assert.Empty(fixture.Executor.Calls);
        Assert.Equal(0, fixture.Executor.VerificationCalls);
    }

    public static TheoryData<InstallerOwnerTransferPhase, bool> MutationFailureCases()
    {
        var result = new TheoryData<InstallerOwnerTransferPhase, bool>();
        foreach (InstallerOwnerTransferPhase phase in Enum.GetValues<InstallerOwnerTransferPhase>()
            .Where(phase => phase > InstallerOwnerTransferPhase.Prepared && phase < InstallerOwnerTransferPhase.Verified))
        {
            result.Add(phase, false);
            result.Add(phase, true);
        }
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        internal MemoryPersistence Persistence { get; }
        internal InstallerOwnerTransferStore Store { get; }
        internal FileInstallerTransactionStore Ordinary { get; }
        internal PhaseExecutor Executor { get; }
        internal InstallerOwnerTransferSnapshot Snapshot => InstallerOwnerTransferSnapshot.Create(InstallerOwnerTransferCodec.Parse(Persistence.Bytes!));
        internal string OrdinaryJournalPath => Path.Combine(_directory, FileInstallerTransactionStore.JournalFileName);

        private Fixture(InstallerOwnerTransferJournal journal)
        {
            _directory = Path.Combine(Path.GetTempPath(), "ClashSharpOwnerTransferCoordinatorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Persistence = new MemoryPersistence { Bytes = InstallerOwnerTransferCodec.Serialize(journal) };
            Store = new InstallerOwnerTransferStore(Persistence);
            Ordinary = new FileInstallerTransactionStore(_directory, new TestRootGuard());
            Executor = new PhaseExecutor(this);
        }

        internal static async Task<Fixture> CreateAsync(InstallerOwnerTransferPhase initial)
        {
            InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.AtPhase(initial);
            var fixture = new Fixture(journal);
            try
            {
                foreach (InstallerOwnerTransferPhase phase in Enum.GetValues<InstallerOwnerTransferPhase>()
                    .Where(phase => phase > InstallerOwnerTransferPhase.Prepared && phase <= initial && phase < InstallerOwnerTransferPhase.Verified))
                {
                    fixture.Executor.Applied.Add(phase);
                }
                if (initial >= InstallerOwnerTransferPhase.StartupBlocked)
                {
                    await fixture.Ordinary.SaveAsync(journal.Continuation, null, CancellationToken.None);
                }
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Ordinary.Dispose();
            string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClashSharpOwnerTransferCoordinatorTests")) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(_directory);
            if (!target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Invalid owned fixture cleanup path.");
            }
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class TestRootGuard : IInstallerTransactionRootGuard
    {
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Directory.Exists(absoluteRootPath));
            return Task.CompletedTask;
        }
    }

    private sealed class PhaseExecutor(Fixture fixture) : IInstallerOwnerTransferPhaseExecutor
    {
        internal List<InstallerOwnerTransferPhase> Calls { get; } = [];
        internal HashSet<InstallerOwnerTransferPhase> Applied { get; } = [];
        internal Dictionary<InstallerOwnerTransferPhase, int> MutationCounts { get; } = [];
        internal InstallerOwnerTransferPhase? FailurePhase { get; set; }
        internal bool FailAfterMutation { get; set; }
        internal bool FailVerification { get; set; }
        internal int VerificationCalls { get; private set; }
        internal Func<CancellationToken, Task>? BeforeApply { get; set; }

        public async Task ApplyAndVerifyAsync(InstallerOwnerTransferJournal current, InstallerOwnerTransferPhase nextPhase, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(current, fixture.Snapshot.Journal);
            Assert.Equal((int)current.Phase + 1, (int)nextPhase);
            Calls.Add(nextPhase);
            if (BeforeApply is not null)
            {
                await BeforeApply(cancellationToken);
            }
            if (FailurePhase == nextPhase && !FailAfterMutation)
            {
                throw new InstallerStateUncertainException("installer.owner_transfer.injected_native_failure");
            }
            if (Applied.Add(nextPhase))
            {
                MutationCounts[nextPhase] = MutationCounts.GetValueOrDefault(nextPhase) + 1;
                if (nextPhase == InstallerOwnerTransferPhase.StartupBlocked)
                {
                    await fixture.Ordinary.SaveAsync(current.Continuation, null, CancellationToken.None);
                }
            }
            if (FailurePhase == nextPhase && FailAfterMutation)
            {
                throw new InstallerStateUncertainException("installer.owner_transfer.injected_native_failure");
            }
            Assert.Equal(current.Continuation, (await fixture.Ordinary.LoadAsync(CancellationToken.None))?.Journal);
        }

        public async Task VerifyCompletedAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerificationCalls++;
            Assert.True(current.Phase >= InstallerOwnerTransferPhase.InstallerAccessTransferred);
            Assert.Equal(current, fixture.Snapshot.Journal);
            Assert.Equal(6, Applied.Count);
            if (FailVerification || (await fixture.Ordinary.LoadAsync(cancellationToken))?.Journal != current.Continuation)
            {
                throw new InstallerProtocolException("installer.owner_transfer.final_postconditions_invalid");
            }
        }
    }

    private sealed class MemoryPersistence : IInstallerOwnerTransferPersistence
    {
        internal byte[]? Bytes { get; set; }
        internal int ReadCalls { get; private set; }
        internal int DeleteCalls { get; private set; }
        internal List<InstallerOwnerTransferPhase> SavedPhases { get; } = [];
        internal InstallerOwnerTransferPhase? FailWritePhase { get; set; }
        internal bool FailDelete { get; set; }

        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return Task.FromResult(Bytes?.ToArray());
        }

        public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallerOwnerTransferJournal journal = InstallerOwnerTransferCodec.Parse(bytes.Span);
            if (FailWritePhase == journal.Phase)
            {
                throw new IOException("Injected write failure.");
            }
            Bytes = bytes.ToArray();
            SavedPhases.Add(journal.Phase);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalls++;
            if (FailDelete)
            {
                throw new IOException("Injected delete failure.");
            }
            Bytes = null;
            return Task.CompletedTask;
        }
    }
}
