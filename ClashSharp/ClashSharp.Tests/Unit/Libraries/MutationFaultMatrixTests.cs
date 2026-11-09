using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Exhaustive fault injection over the journaled mutation coordinator. A fault (exception, cancellation or
/// process-fatal failure) is injected at every participant call, plan hook and journal operation; whatever the
/// coordinator reports, the combined world must end either fully at the baseline or fully at the desired state
/// once recovery has run, and admission must be reopened only after a verified outcome.
/// </summary>
public sealed class MutationFaultMatrixTests
{
    /// <summary>Verifies a single fault at every effect boundary converges to an old-or-new state with the right admission state.</summary>
    /// <param name="participants">Number of participants in the plan.</param>
    /// <param name="kind">Injected fault.</param>
    [Theory]
    [InlineData(1, Fault.Throw)]
    [InlineData(1, Fault.Cancel)]
    [InlineData(2, Fault.Throw)]
    [InlineData(2, Fault.Cancel)]
    [InlineData(3, Fault.Throw)]
    public async Task SingleFault_AtEveryEffect_ConvergesToTheOldOrTheNewStateNeverAMix(int participants, Fault kind)
    {
        World clean = new(participants);
        MutationResult<string> baselineRun = await clean.RunAsync();
        Assert.Equal(MutationOutcome.Succeeded, baselineRun.Outcome);
        clean.AssertSettled(expectDesired: true);
        int effects = clean.EffectCount;
        Assert.True(effects > 20 * participants / 2, "the clean run must exercise a real effect universe");
        HashSet<MutationOutcome> outcomes = [];

        for (int fault = 1; fault <= effects; fault++)
        {
            World world = new(participants);
            world.Schedule(fault, kind);

            MutationResult<string> result = await world.RunAsync();
            outcomes.Add(result.Outcome);

            await world.AssertConvergesAsync(result, $"fault {fault}/{kind} at {clean.Trace[fault - 1]}");
        }

        Assert.Contains(MutationOutcome.Compensated, outcomes);
        if (kind == Fault.Throw)
        {
            Assert.DoesNotContain(MutationOutcome.Succeeded, outcomes);
            Assert.Contains(outcomes, outcome => outcome is MutationOutcome.RecoveryRequired or MutationOutcome.CommittedRecoveryRequired);
            Assert.Contains(MutationOutcome.Failed, outcomes);
        }
    }

    /// <summary>Verifies two faults (the second during compensation or recovery) are always repairable by retried recovery.</summary>
    /// <param name="participants">Number of participants in the plan.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PairedFaults_NeverStrandTheWorldInAMixedStateAfterRecoveryRetries(int participants)
    {
        World clean = new(participants);
        await clean.RunAsync();
        int effects = clean.EffectCount;
        int retained = 0;

        for (int first = 1; first <= effects; first++)
        {
            for (int second = first + 1; second <= effects + 12 + (4 * participants); second++)
            {
                World world = new(participants);
                world.Schedule(first, Fault.Throw);
                world.Schedule(second, Fault.Throw);

                MutationResult<string> result = await world.RunAsync();
                retained += result.Outcome is MutationOutcome.RecoveryRequired or MutationOutcome.CommittedRecoveryRequired ? 1 : 0;

                await world.AssertConvergesAsync(result, $"faults {first}+{second}");
            }
        }

        Assert.True(retained > 0, "double faults must produce retained recovery states, otherwise the matrix is vacuous");
    }

    /// <summary>Verifies recovery is itself restartable: faults injected into recovery attempts never corrupt the journal or world.</summary>
    [Fact]
    public async Task RandomFaultSequences_IncludingFaultsDuringRecovery_AlwaysConverge()
    {
        Random random = new(SettingsGenerators.Seed);
        World probe = new(2);
        await probe.RunAsync();
        int universe = probe.EffectCount + 24;

        for (int round = 0; round < 400; round++)
        {
            World world = new(2);
            int count = 1 + random.Next(4);
            for (int index = 0; index < count; index++)
            {
                world.Schedule(1 + random.Next(universe), random.Next(5) == 0 ? Fault.Cancel : Fault.Throw);
            }

            MutationResult<string> result = await world.RunAsync();
            int recoveryAttempts = 0;
            while (world.Barrier.State != MutationAdmissionState.Open && recoveryAttempts++ < 6)
            {
                MutationResult<object?> recovery = await world.Coordinator.RetryRecoveryAsync(world.OperationId, CancellationToken.None);
                Assert.True(
                    recovery.Outcome is MutationOutcome.Succeeded or MutationOutcome.Compensated
                        or MutationOutcome.RecoveryRequired or MutationOutcome.Cancelled,
                    recovery.Outcome.ToString());
                world.AssertBarrierMatchesJournal();
            }

            Assert.True(
                world.Store.Current is null && world.Barrier.State == MutationAdmissionState.Open,
                $"round {round}: recovery must converge once faults are exhausted (last {result.Outcome})");
            world.AssertSettled(expectDesired: world.Target == "desired");
            Assert.Equal(MutationAdmissionState.Open, world.Barrier.State);
        }
    }

    /// <summary>Verifies a process-fatal failure at any boundary forces recovery-only admission and recovery converges.</summary>
    [Fact]
    public async Task ProcessFatalFailure_AtEveryEffect_ForcesRecoveryOnlyAndRecoveryConverges()
    {
        World clean = new(2);
        await clean.RunAsync();

        for (int fault = 1; fault <= clean.EffectCount; fault++)
        {
            World world = new(2);
            world.Schedule(fault, Fault.Fatal);

            await Assert.ThrowsAsync<OutOfMemoryException>(() => world.RunAsync());

            Assert.Equal(MutationAdmissionState.RecoveryOnly, world.Barrier.State);
            Assert.False(world.Gate.IsHeld);
            Assert.Throws<MutationAdmissionRejectedException>(() => world.Barrier.AcquireOrdinary());
            MutationResult<object?> recovery = await world.Coordinator.RetryRecoveryAsync(world.OperationId, CancellationToken.None);
            Assert.True(recovery.Outcome is MutationOutcome.Succeeded or MutationOutcome.Compensated, $"fatal at {fault}: {recovery.Outcome}");
            Assert.Null(world.Store.Current);
            Assert.Equal(MutationAdmissionState.Open, world.Barrier.State);
            world.AssertSettled(expectDesired: world.Target == "desired");
        }
    }

    /// <summary>Verifies a retained recovery journal blocks every other admission class until a verified recovery completes.</summary>
    [Fact]
    public async Task RetainedRecovery_BlocksOrdinaryAndDestructiveAdmissionAndRejectsForeignRecoveryIdentity()
    {
        World world = new(2);
        world.Schedule(5, Fault.Throw);
        world.Schedule(8, Fault.Throw);
        world.Schedule(9, Fault.Throw);
        world.Schedule(10, Fault.Throw);
        MutationResult<string> failed = await world.RunAsync();
        Assert.Equal(MutationOutcome.RecoveryRequired, failed.Outcome);
        Assert.NotNull(world.Store.Current);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, world.Barrier.State);

        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(() => world.RunAsync(new Guid("11111111-0000-0000-0000-000000000001")));
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(() => world.RunAsync(new Guid("11111111-0000-0000-0000-000000000002"), quiescence: true));
        world.ClearFaults();
        MutationResult<object?> foreign = await world.Coordinator.RetryRecoveryAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(MutationOutcome.RecoveryRequired, foreign.Outcome);
        Assert.Equal("recovery-operation-mismatch", foreign.ErrorCode);
        Assert.NotNull(world.Store.Current);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, world.Barrier.State);

        MutationResult<object?> recovered = await world.Coordinator.RetryRecoveryAsync(world.OperationId, CancellationToken.None);
        Assert.True(recovered.Outcome is MutationOutcome.Succeeded or MutationOutcome.Compensated);
        Assert.Null(world.Store.Current);
        Assert.Equal(MutationAdmissionState.Open, world.Barrier.State);
        world.AssertSettled(expectDesired: world.Target == "desired");
        MutationResult<string> next = await world.RunAsync(new Guid("11111111-0000-0000-0000-000000000003"));
        Assert.Equal(MutationOutcome.Succeeded, next.Outcome);
    }

    /// <summary>Verifies a quiescent (destructive) request obeys the same fault contract and always reopens admission.</summary>
    [Fact]
    public async Task DestructiveRequest_SingleFaultAtEveryEffect_ConvergesAndReopensAdmission()
    {
        World clean = new(2);
        MutationResult<string> baseline = await clean.RunAsync(quiescence: true);
        Assert.Equal(MutationOutcome.Succeeded, baseline.Outcome);
        Assert.Equal(MutationAdmissionState.Open, clean.Barrier.State);

        for (int fault = 1; fault <= clean.EffectCount; fault++)
        {
            World world = new(2);
            world.Schedule(fault, Fault.Throw);

            MutationResult<string> result = await world.RunAsync(quiescence: true);

            await world.AssertConvergesAsync(result, $"destructive fault {fault}");
        }
    }

    /// <summary>Verifies pre-cancelled callers never create a journal or touch a participant, and release admission.</summary>
    [Fact]
    public async Task AlreadyCancelledCaller_HasNoSideEffectsAndLeavesAdmissionOpen()
    {
        foreach (bool quiescence in new[] { false, true })
        {
            World world = new(2);
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            MutationResult<string> result = await world.RunAsync(quiescence: quiescence, token: cancellation.Token);

            Assert.Equal(MutationOutcome.Cancelled, result.Outcome);
            Assert.Equal(0, world.EffectCount);
            Assert.Null(world.Store.Current);
            Assert.Equal(MutationAdmissionState.Open, world.Barrier.State);
            world.AssertSettled(expectDesired: false);
        }
    }

    /// <summary>Kinds of injected fault.</summary>
    public enum Fault
    {
        /// <summary>An ordinary exception from the failing dependency.</summary>
        Throw,

        /// <summary>The caller's token is cancelled at the boundary.</summary>
        Cancel,

        /// <summary>A process-fatal exception.</summary>
        Fatal,
    }

    private sealed class World
    {
        private readonly Dictionary<int, Fault> _faults = [];
        private readonly CancellationTokenSource _cancellation = new();
        private readonly List<WorldParticipant> _participants;

        public World(int participants)
        {
            Store = new WorldStore(this);
            _participants = [.. Enumerable.Range(1, participants).Select(index => new WorldParticipant(this, "participant-" + index))];
            Coordinator = new ApplicationMutationCoordinator(
                Barrier,
                Gate,
                Store,
                new Resolver(this),
                new MutationDeadlines(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)));
        }

        public MutationAdmissionBarrier Barrier { get; } = new();

        public FairAsyncMutationGate Gate { get; } = new();

        public WorldStore Store { get; }

        public ApplicationMutationCoordinator Coordinator { get; }

        public Guid OperationId { get; } = new("22222222-0000-0000-0000-000000000001");

        public string Target { get; set; } = "baseline";

        public List<string> Trace { get; } = [];

        public int EffectCount { get; private set; }

        public IReadOnlyList<WorldParticipant> Participants => _participants;

        public void Schedule(int effect, Fault kind) => _faults[effect] = kind;

        public void ClearFaults() => _faults.Clear();

        public void Effect(string name, CancellationToken token, bool store = false)
        {
            EffectCount++;
            Trace.Add(name);
            if (!_faults.TryGetValue(EffectCount, out Fault kind))
            {
                return;
            }

            switch (kind)
            {
                case Fault.Throw:
                    throw store
                        ? new MutationJournalStoreException(MutationJournalStoreError.ConcurrencyConflict, "injected store fault")
                        : new IOException("injected fault at " + name);
                case Fault.Cancel:
                    _cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    break;
                default:
                    throw Activator.CreateInstance<OutOfMemoryException>();
            }
        }

        public MutationPlan CreatePlan() => new(
            "baseline-hash",
            "desired-hash",
            _participants,
            token =>
            {
                Effect("validate", token);
                return Task.CompletedTask;
            },
            (_, token) =>
            {
                Effect("promote", token);
                Target = "desired";
                return Task.CompletedTask;
            },
            (_, token) =>
            {
                Effect("restore-baseline", token);
                Target = "baseline";
                return Task.CompletedTask;
            },
            (_, token) =>
            {
                Effect("verify-target", token);
                return Target == "desired" ? Task.CompletedTask : throw new InvalidOperationException("target is not desired");
            },
            (_, token) =>
            {
                Effect("verify-baseline", token);
                return Target == "baseline" ? Task.CompletedTask : throw new InvalidOperationException("target is not baseline");
            });

        public Task<MutationResult<string>> RunAsync(Guid? operationId = null, bool quiescence = false, CancellationToken? token = null) =>
            Coordinator.ExecuteAsync(
                new MutationRequest(operationId ?? OperationId, "fault-matrix", quiescence),
                CreatePlan(),
                (_, _) => Task.FromResult("done"),
                token ?? _cancellation.Token);

        public void AssertSettled(bool expectDesired)
        {
            string expected = expectDesired ? "desired" : "baseline";
            Assert.Equal(expected, Target);
            Assert.All(_participants, participant => Assert.Equal(expected, participant.State));
        }

        public void AssertBarrierMatchesJournal()
        {
            if (Store.Current is not null)
            {
                Assert.NotEqual(MutationAdmissionState.Open, Barrier.State);
            }
        }

        public async Task AssertConvergesAsync(MutationResult<string> result, string context)
        {
            Assert.False(Gate.IsHeld, context);
            Assert.Equal(0, Gate.QueuedCount);
            switch (result.Outcome)
            {
                case MutationOutcome.Succeeded:
                    Assert.Null(Store.Current);
                    AssertSettled(expectDesired: true);
                    Assert.Equal(MutationAdmissionState.Open, Barrier.State);
                    break;
                case MutationOutcome.Cancelled:
                case MutationOutcome.Compensated:
                case MutationOutcome.Failed:
                    Assert.Null(Store.Current);
                    AssertSettled(expectDesired: false);
                    Assert.Equal(MutationAdmissionState.Open, Barrier.State);
                    break;
                case MutationOutcome.RecoveryRequired:
                case MutationOutcome.CommittedRecoveryRequired:
                    Assert.Equal(MutationAdmissionState.RecoveryOnly, Barrier.State);
                    Assert.Throws<MutationAdmissionRejectedException>(() => Barrier.AcquireOrdinary());
                    if (result.Outcome == MutationOutcome.CommittedRecoveryRequired)
                    {
                        Assert.True(Store.Current!.Journal.HasCommitMarker, context);
                    }

                    ClearFaults();
                    MutationResult<object?> recovery = await Coordinator.RetryRecoveryAsync(OperationId, CancellationToken.None);
                    Assert.True(recovery.Outcome is MutationOutcome.Succeeded or MutationOutcome.Compensated, $"{context}: {recovery.Outcome}");
                    Assert.Null(Store.Current);
                    Assert.Equal(MutationAdmissionState.Open, Barrier.State);
                    AssertSettled(expectDesired: Target == "desired");
                    if (result.Outcome == MutationOutcome.CommittedRecoveryRequired)
                    {
                        Assert.Equal(MutationOutcome.Succeeded, recovery.Outcome);
                        Assert.Equal("desired", Target);
                    }

                    break;
                default:
                    throw new NotSupportedException($"{context}: unclassified outcome {result.Outcome}");
            }

            using MutationAdmissionLease lease = Barrier.AcquireOrdinary();
            Assert.False(lease.IsExclusive);
        }
    }

    private sealed class WorldParticipant : IApplicationMutationParticipant
    {
        private readonly World _world;

        public WorldParticipant(World world, string name)
        {
            _world = world;
            Name = name;
        }

        public string State { get; private set; } = "baseline";

        public string Name { get; }

        public string? CompensationData => "restore-" + Name;

        public Task<MutationProbeState> ProbeAsync(MutationContext context, CancellationToken cancellationToken)
        {
            _world.Effect(Name + ":probe", cancellationToken);
            return Task.FromResult(State switch
            {
                "baseline" => MutationProbeState.Baseline,
                "desired" => MutationProbeState.Desired,
                _ => MutationProbeState.Partial,
            });
        }

        public Task StageAsync(MutationContext context, CancellationToken cancellationToken)
        {
            _world.Effect(Name + ":stage", cancellationToken);
            State = "partial";
            return Task.CompletedTask;
        }

        public Task ApplyAsync(MutationContext context, CancellationToken cancellationToken)
        {
            _world.Effect(Name + ":apply", cancellationToken);
            State = "desired";
            return Task.CompletedTask;
        }

        public Task VerifyAsync(MutationContext context, CancellationToken cancellationToken)
        {
            _world.Effect(Name + ":verify", cancellationToken);
            return State == "desired" ? Task.CompletedTask : throw new InvalidOperationException("participant is not desired");
        }

        public Task CompensateAsync(MutationContext context, CancellationToken cancellationToken)
        {
            _world.Effect(Name + ":compensate", cancellationToken);
            State = "baseline";
            return Task.CompletedTask;
        }

        public Task ActivateAsync(MutationContext context, CancellationToken cancellationToken)
        {
            _world.Effect(Name + ":activate", cancellationToken);
            return Task.CompletedTask;
        }

        public Task CleanupAsync(MutationContext context, CancellationToken cancellationToken)
        {
            _world.Effect(Name + ":cleanup", cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class Resolver(World world) : IMutationRecoveryPlanResolver
    {
        public Task<MutationPlan> ResolveAsync(MutationJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(world.CreatePlan());
        }
    }

    private sealed class WorldStore(World world) : IMutationJournalStore
    {
        public MutationJournalSnapshot? Current { get; private set; }

        public Task<MutationJournalSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            world.Effect("store:load", cancellationToken, store: true);
            return Task.FromResult(Current);
        }

        public Task<MutationJournalSnapshot> SaveAsync(MutationJournal journal, string? expectedCurrentHash, CancellationToken cancellationToken)
        {
            world.Effect("store:save", cancellationToken, store: true);
            if (!string.Equals(Current?.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                throw new MutationJournalStoreException(MutationJournalStoreError.ConcurrencyConflict, "unexpected hash");
            }

            if (journal.Generation != (Current?.Journal.Generation ?? 0) + 1)
            {
                throw new MutationJournalStoreException(MutationJournalStoreError.InvalidGeneration, "unexpected generation");
            }

            Current = new MutationJournalSnapshot(journal, $"hash-{journal.Generation}");
            return Task.FromResult(Current);
        }

        public Task DeleteAsync(Guid operationId, string expectedCurrentHash, CancellationToken cancellationToken)
        {
            world.Effect("store:delete", cancellationToken, store: true);
            if (Current?.Journal.OperationId != operationId || !string.Equals(Current.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                throw new MutationJournalStoreException(MutationJournalStoreError.ConcurrencyConflict, "unexpected delete identity");
            }

            Current = null;
            return Task.CompletedTask;
        }
    }
}
