using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies journal-driven mutation execution, compensation, and same-process recovery.</summary>
public sealed class ApplicationMutationCoordinatorTests
{
    /// <summary>Verifies a successful mutation flushes, verifies, commits, activates, cleans, and removes its journal.</summary>
    [Fact]
    public async Task ExecuteAsync_Success_CommitsVerifiedTargetAndRemovesJournal()
    {
        Fixture fixture = new();

        MutationResult<string> result = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) =>
            {
                fixture.Trace.Add("result");
                return Task.FromResult("verified-result");
            },
            CancellationToken.None);

        Assert.Equal(MutationOutcome.Succeeded, result.Outcome);
        Assert.Equal("verified-result", result.Value);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(MutationAdmissionState.Open, fixture.Barrier.State);
        Assert.Equal(
            ["validate", "stage", "apply", "verify", "promote", "verify-target", "activate", "verify", "verify-target", "cleanup", "result"],
            fixture.Trace);
        Assert.True(fixture.Store.SaveCount >= 8);
    }

    /// <summary>Verifies a pre-journal planning failure cannot create recovery state or call a participant.</summary>
    [Fact]
    public async Task ExecuteAsync_PlanningFailure_FailsWithoutJournalOrSideEffect()
    {
        Fixture fixture = new();
        fixture.ValidateException = new InvalidOperationException("invalid plan");

        MutationResult<string> result = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("unused"),
            CancellationToken.None);

        Assert.Equal(MutationOutcome.Failed, result.Outcome);
        Assert.Equal("mutation-plan-failed", result.ErrorCode);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(["validate"], fixture.Trace);
    }

    /// <summary>Verifies an uncommitted apply failure restores and verifies the baseline with an independent token.</summary>
    [Fact]
    public async Task ExecuteAsync_ApplyFailure_CompensatesAndVerifiesBaseline()
    {
        Fixture fixture = new();
        fixture.Participant.ApplyException = new InvalidOperationException("apply failed");

        MutationResult<string> result = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("unused"),
            CancellationToken.None);

        Assert.Equal(MutationOutcome.Compensated, result.Outcome);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(MutationAdmissionState.Open, fixture.Barrier.State);
        Assert.Contains("compensate", fixture.Trace);
        Assert.Contains("restore-baseline", fixture.Trace);
        Assert.Contains("verify-baseline", fixture.Trace);
        Assert.False(fixture.Participant.LastCompensationTokenWasCancelled);
    }

    /// <summary>Verifies cancellation after the first side effect requests bounded compensation instead of cancelling rollback.</summary>
    [Fact]
    public async Task ExecuteAsync_CallerCancelsAfterStage_CompensatesWithIndependentToken()
    {
        using CancellationTokenSource callerCancellation = new();
        Fixture fixture = new();
        fixture.Participant.AfterStage = callerCancellation.Cancel;

        MutationResult<string> result = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("unused"),
            callerCancellation.Token);

        Assert.Equal(MutationOutcome.Compensated, result.Outcome);
        Assert.Null(fixture.Store.Current);
        Assert.Contains("compensate", fixture.Trace);
        Assert.False(fixture.Participant.LastCompensationTokenWasCancelled);
    }

    /// <summary>Verifies failed recovery retains and advances one journal until a later same-process retry succeeds.</summary>
    [Fact]
    public async Task RetryRecoveryAsync_FirstRetryFails_SecondRetryUsesLatestGenerationAndReopensAdmission()
    {
        Fixture fixture = new();
        fixture.Participant.ApplyException = new InvalidOperationException("apply failed");
        fixture.Participant.CompensationFailuresRemaining = 2;

        MutationResult<string> initial = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("unused"),
            CancellationToken.None);
        Assert.Equal(MutationOutcome.RecoveryRequired, initial.Outcome);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Barrier.State);
        long initialGeneration = fixture.Store.Current!.Journal.Generation;
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await fixture.Barrier.AcquireOrdinaryAsync(CancellationToken.None));

        MutationResult<object?> firstRetry = await fixture.Coordinator.RetryRecoveryAsync(
            fixture.Request.OperationId,
            CancellationToken.None);
        Assert.Equal(MutationOutcome.RecoveryRequired, firstRetry.Outcome);
        Assert.NotNull(fixture.Store.Current);
        Assert.True(fixture.Store.Current.Journal.Generation > initialGeneration);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Barrier.State);

        MutationResult<object?> secondRetry = await fixture.Coordinator.RetryRecoveryAsync(
            fixture.Request.OperationId,
            CancellationToken.None);
        Assert.Equal(MutationOutcome.Compensated, secondRetry.Outcome);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(MutationAdmissionState.Open, fixture.Barrier.State);
    }

    /// <summary>Verifies a post-commit cleanup failure can only recover forward and blocks unrelated work until cleanup succeeds.</summary>
    [Fact]
    public async Task ExecuteAsync_PostCommitCleanupFails_RetryCompletesForwardWithoutCompensation()
    {
        Fixture fixture = new();
        fixture.Participant.CleanupFailuresRemaining = 1;

        MutationResult<string> initial = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("committed"),
            CancellationToken.None);

        Assert.Equal(MutationOutcome.CommittedRecoveryRequired, initial.Outcome);
        Assert.True(fixture.Store.Current!.Journal.HasCommitMarker);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Barrier.State);
        int compensationCalls = fixture.Trace.Count(item => item == "compensate");

        MutationResult<object?> recovered = await fixture.Coordinator.RetryRecoveryAsync(
            fixture.Request.OperationId,
            CancellationToken.None);

        Assert.Equal(MutationOutcome.Succeeded, recovered.Outcome);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(MutationAdmissionState.Open, fixture.Barrier.State);
        Assert.Equal(compensationCalls, fixture.Trace.Count(item => item == "compensate"));
        Assert.True(fixture.Trace.Count(item => item == "cleanup") >= 2);
    }

    /// <summary>Verifies retained recovery closes admission before the fair gate can hand off to an admitted waiter.</summary>
    [Fact]
    public async Task ExecuteAsync_RetainsRecovery_CancelsAlreadyAdmittedGateWaiterBeforeItMutates()
    {
        Fixture fixture = new();
        fixture.Participant.ApplyException = new InvalidOperationException("apply failed");
        fixture.Participant.CompensationFailuresRemaining = 1;
        fixture.Participant.StageEntered = CreateSignal();
        fixture.Participant.ReleaseStage = CreateSignal();

        Task<MutationResult<string>> firstTask = fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("unused"),
            CancellationToken.None);
        await fixture.Participant.StageEntered.Task;

        List<string> secondTrace = [];
        FakeParticipant secondParticipant = new(secondTrace);
        MutationPlan secondPlan = new(
            "second-baseline",
            "second-desired",
            [secondParticipant],
            _ =>
            {
                secondTrace.Add("validate");
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        MutationRequest secondRequest = new(Guid.NewGuid(), "second-mutation");
        Task<MutationResult<string>> secondTask = fixture.Coordinator.ExecuteAsync(
            secondRequest,
            secondPlan,
            (_, _) => Task.FromResult("second"),
            CancellationToken.None);
        Assert.Equal(1, fixture.Gate.QueuedCount);

        fixture.Participant.ReleaseStage.SetResult(null);
        MutationResult<string>[] results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(MutationOutcome.RecoveryRequired, results[0].Outcome);
        Assert.Equal(MutationOutcome.Cancelled, results[1].Outcome);
        Assert.Empty(secondTrace);
        Assert.Equal(fixture.Request.OperationId, fixture.Store.Current!.Journal.OperationId);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Barrier.State);
    }

    /// <summary>Verifies an exclusive destructive request revokes an in-flight ordinary request and starts only after compensation drains.</summary>
    [Fact]
    public async Task ExecuteAsync_DestructiveRequest_RevokesCompensatesAndDrainsOrdinaryMutation()
    {
        Fixture fixture = new();
        fixture.Participant.StageEntered = CreateSignal();
        fixture.Participant.ReleaseStage = CreateSignal();

        Task<MutationResult<string>> ordinaryTask = fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("ordinary"),
            CancellationToken.None);
        await fixture.Participant.StageEntered.Task;

        List<string> destructiveTrace = [];
        FakeParticipant destructiveParticipant = new(destructiveTrace);
        MutationPlan destructivePlan = new(
            "destructive-baseline",
            "destructive-desired",
            [destructiveParticipant],
            _ =>
            {
                destructiveTrace.Add("validate");
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        MutationRequest destructiveRequest = new(Guid.NewGuid(), "destructive-mutation", RequiresQuiescence: true);
        Task<MutationResult<string>> destructiveTask = fixture.Coordinator.ExecuteAsync(
            destructiveRequest,
            destructivePlan,
            (_, _) => Task.FromResult("destructive"),
            CancellationToken.None);

        Assert.Equal(MutationAdmissionState.Closing, fixture.Barrier.State);
        Assert.Empty(destructiveTrace);
        fixture.Participant.ReleaseStage.SetResult(null);

        MutationResult<string> ordinary = await ordinaryTask;
        MutationResult<string> destructive = await destructiveTask;
        Assert.Equal(MutationOutcome.Compensated, ordinary.Outcome);
        Assert.Equal(MutationOutcome.Succeeded, destructive.Outcome);
        Assert.Contains("compensate", fixture.Trace);
        Assert.Contains("stage", destructiveTrace);
        Assert.Equal(MutationAdmissionState.Open, fixture.Barrier.State);
        Assert.Null(fixture.Store.Current);
    }

    /// <summary>Verifies an unexpected pre-existing journal blocks a new mutation instead of reporting a clean planning failure.</summary>
    [Fact]
    public async Task ExecuteAsync_PreExistingJournal_EntersRecoveryOnlyBeforeParticipantSideEffects()
    {
        Fixture fixture = new();
        MutationJournal retained = new(
            MutationJournal.CurrentSchemaVersion,
            Guid.NewGuid(),
            "retained-operation",
            Generation: 1,
            MutationJournalPhase.Applying,
            "retained-baseline",
            "retained-desired",
            HasCommitMarker: false,
            [new MutationJournalStep("retained", MutationJournalPhase.Applying, true, false, "retained-data")]);
        fixture.Store.Seed(retained);

        MutationResult<string> result = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("unused"),
            CancellationToken.None);

        Assert.Equal(MutationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Barrier.State);
        Assert.Equal(retained.OperationId, fixture.Store.Current!.Journal.OperationId);
        Assert.DoesNotContain("stage", fixture.Trace);
    }

    /// <summary>Verifies a timed-out external step is compensated with the still-live total recovery token.</summary>
    [Fact]
    public async Task ExecuteAsync_StepTimeout_CompensatesWithinIndependentTotalDeadline()
    {
        Fixture fixture = new(new MutationDeadlines(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(2)));
        fixture.Participant.StageEntered = CreateSignal();
        fixture.Participant.ReleaseStage = CreateSignal();

        MutationResult<string> result = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("unused"),
            CancellationToken.None);

        Assert.Equal(MutationOutcome.Compensated, result.Outcome);
        Assert.Equal("mutation-compensated-timeout", result.ErrorCode);
        Assert.False(fixture.Participant.LastCompensationTokenWasCancelled);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(MutationAdmissionState.Open, fixture.Barrier.State);
    }

    /// <summary>Verifies recovery rejects a resolver plan whose hashes or compensation identity differ from the journal.</summary>
    [Fact]
    public async Task RetryRecoveryAsync_MismatchedResolvedPlan_RetainsLatestJournalWithoutSideEffects()
    {
        Fixture fixture = new();
        fixture.Participant.ApplyException = new InvalidOperationException("apply failed");
        fixture.Participant.CompensationFailuresRemaining = 1;
        MutationResult<string> initial = await fixture.Coordinator.ExecuteAsync(
            fixture.Request,
            fixture.Plan,
            (_, _) => Task.FromResult("unused"),
            CancellationToken.None);
        Assert.Equal(MutationOutcome.RecoveryRequired, initial.Outcome);
        long generation = fixture.Store.Current!.Journal.Generation;

        fixture.Resolver.Plan = new MutationPlan(
            "foreign-baseline",
            "foreign-desired",
            [fixture.Participant],
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        int compensationCalls = fixture.Trace.Count(item => item == "compensate");

        MutationResult<object?> retry = await fixture.Coordinator.RetryRecoveryAsync(
            fixture.Request.OperationId,
            CancellationToken.None);

        Assert.Equal(MutationOutcome.RecoveryRequired, retry.Outcome);
        Assert.Equal(generation, fixture.Store.Current!.Journal.Generation);
        Assert.Equal(compensationCalls, fixture.Trace.Count(item => item == "compensate"));
        Assert.Equal(MutationAdmissionState.RecoveryOnly, fixture.Barrier.State);
    }

    private sealed class Fixture
    {
        public Fixture(MutationDeadlines? deadlines = null)
        {
            Participant = new FakeParticipant(Trace);
            Plan = new MutationPlan(
                "baseline-hash",
                "desired-hash",
                [Participant],
                ValidateAsync,
                (_, _) => AddCallAsync("promote"),
                (_, _) => AddCallAsync("restore-baseline"),
                (_, _) => AddCallAsync("verify-target"),
                (_, _) => AddCallAsync("verify-baseline"));
            Resolver = new FakeMutationRecoveryPlanResolver(Plan);
            Coordinator = new ApplicationMutationCoordinator(
                Barrier,
                Gate,
                Store,
                Resolver,
                deadlines ?? new MutationDeadlines(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        }

        public List<string> Trace { get; } = [];

        public MutationAdmissionBarrier Barrier { get; } = new();

        public FairAsyncMutationGate Gate { get; } = new();

        public MemoryMutationJournalStore Store { get; } = new();

        public FakeParticipant Participant { get; }

        public FakeMutationRecoveryPlanResolver Resolver { get; }

        public ApplicationMutationCoordinator Coordinator { get; }

        public MutationRequest Request { get; } = new(Guid.NewGuid(), "test-mutation");

        public MutationPlan Plan { get; }

        public Exception? ValidateException { get; set; }

        private Task ValidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace.Add("validate");
            return ValidateException is null
                ? Task.CompletedTask
                : Task.FromException(ValidateException);
        }

        private Task AddCallAsync(string call)
        {
            Trace.Add(call);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeParticipant(List<string> trace) : IApplicationMutationParticipant
    {
        private MutationProbeState _probeState = MutationProbeState.Baseline;

        public string Name => "participant";

        public string? CompensationData => "restore-participant";

        public Exception? ApplyException { get; set; }

        public Action? AfterStage { get; set; }

        public int CompensationFailuresRemaining { get; set; }

        public int CleanupFailuresRemaining { get; set; }

        public bool LastCompensationTokenWasCancelled { get; private set; }

        public TaskCompletionSource<object?>? StageEntered { get; set; }

        public TaskCompletionSource<object?>? ReleaseStage { get; set; }

        public Task<MutationProbeState> ProbeAsync(MutationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("probe");
            return Task.FromResult(_probeState);
        }

        public async Task StageAsync(MutationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("stage");
            _probeState = MutationProbeState.Partial;
            StageEntered?.TrySetResult(null);
            if (ReleaseStage is not null)
            {
                await ReleaseStage.Task.WaitAsync(cancellationToken);
            }

            AfterStage?.Invoke();
        }

        public Task ApplyAsync(MutationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("apply");
            if (ApplyException is not null)
            {
                return Task.FromException(ApplyException);
            }

            _probeState = MutationProbeState.Desired;
            return Task.CompletedTask;
        }

        public Task VerifyAsync(MutationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("verify");
            return _probeState == MutationProbeState.Desired
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("desired state not observed"));
        }

        public Task CompensateAsync(MutationContext context, CancellationToken cancellationToken)
        {
            trace.Add("compensate");
            LastCompensationTokenWasCancelled = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            if (CompensationFailuresRemaining > 0)
            {
                CompensationFailuresRemaining--;
                return Task.FromException(new InvalidOperationException("compensation failed"));
            }

            _probeState = MutationProbeState.Baseline;
            return Task.CompletedTask;
        }

        public Task ActivateAsync(MutationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("activate");
            return Task.CompletedTask;
        }

        public Task CleanupAsync(MutationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("cleanup");
            if (CleanupFailuresRemaining > 0)
            {
                CleanupFailuresRemaining--;
                return Task.FromException(new InvalidOperationException("cleanup failed"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeMutationRecoveryPlanResolver(MutationPlan plan) : IMutationRecoveryPlanResolver
    {
        public MutationPlan Plan { get; set; } = plan;

        public Task<MutationPlan> ResolveAsync(MutationJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Plan);
        }
    }

    private sealed class MemoryMutationJournalStore : IMutationJournalStore
    {
        public MutationJournalSnapshot? Current { get; private set; }

        public int SaveCount { get; private set; }

        public void Seed(MutationJournal journal)
        {
            Current = new MutationJournalSnapshot(journal, $"hash-{journal.Generation}");
        }

        public Task<MutationJournalSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task<MutationJournalSnapshot> SaveAsync(
            MutationJournal journal,
            string? expectedCurrentHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Current?.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                throw new MutationJournalStoreException(
                    MutationJournalStoreError.ConcurrencyConflict,
                    "unexpected hash");
            }

            if (journal.Generation != (Current?.Journal.Generation ?? 0) + 1)
            {
                throw new MutationJournalStoreException(
                    MutationJournalStoreError.InvalidGeneration,
                    "unexpected generation");
            }

            SaveCount++;
            Current = new MutationJournalSnapshot(journal, $"hash-{journal.Generation}");
            return Task.FromResult(Current);
        }

        public Task DeleteAsync(Guid operationId, string expectedCurrentHash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current?.Journal.OperationId != operationId
                || !string.Equals(Current.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                throw new MutationJournalStoreException(
                    MutationJournalStoreError.ConcurrencyConflict,
                    "unexpected delete identity");
            }

            Current = null;
            return Task.CompletedTask;
        }
    }

    private static TaskCompletionSource<object?> CreateSignal()
    {
        return new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
