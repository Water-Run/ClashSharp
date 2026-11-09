using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Treats the admission barrier as a state machine: every (scenario x operation) pair is checked against an
/// explicit legality oracle, and seeded random operation sequences must keep the safety invariants (mutual
/// exclusion of lease kinds, absorbing shutdown, revocation of pre-close leases, idempotent disposal).
/// </summary>
public sealed class MutationAdmissionBarrierStateMachineTests
{
    /// <summary>Operations the barrier exposes.</summary>
    public enum BarrierOperation
    {
        /// <summary>AcquireOrdinary.</summary>
        AcquireOrdinary,

        /// <summary>CloseAndDrain with the destructive closure.</summary>
        CloseDestructive,

        /// <summary>CloseAndDrain with the shutdown closure.</summary>
        CloseShutdown,

        /// <summary>EnterRecoveryOnly.</summary>
        EnterRecoveryOnly,

        /// <summary>AcquireRecovery.</summary>
        AcquireRecovery,

        /// <summary>AcquireShutdownMaintenance.</summary>
        AcquireShutdownMaintenance,

        /// <summary>RequestRecoveryShutdown.</summary>
        RequestRecoveryShutdown,
    }

    /// <summary>Reachable barrier configurations.</summary>
    public enum Scenario
    {
        /// <summary>Open, nothing held.</summary>
        Open,

        /// <summary>Open with one ordinary lease held.</summary>
        OpenWithOrdinary,

        /// <summary>A destructive close is waiting for the ordinary lease to drain.</summary>
        ClosingDraining,

        /// <summary>The destructive exclusive lease is held.</summary>
        ClosingExclusive,

        /// <summary>Recovery-only, no recovery attempt running.</summary>
        RecoveryOnly,

        /// <summary>Recovery-only with the recovery lease held.</summary>
        RecoveryOnlyExclusive,

        /// <summary>A shutdown was requested while the recovery lease is held.</summary>
        RecoveryClosing,

        /// <summary>Closed for shutdown, no maintenance lease held.</summary>
        ClosedForShutdown,

        /// <summary>Closed for shutdown with the maintenance lease held.</summary>
        ClosedForShutdownExclusive,
    }

    /// <summary>Enumerates every scenario x operation pair from the enum definitions.</summary>
    /// <returns>Theory rows.</returns>
    public static TheoryData<Scenario, BarrierOperation> Universe()
    {
        TheoryData<Scenario, BarrierOperation> data = [];
        foreach (Scenario scenario in Enum.GetValues<Scenario>())
        {
            foreach (BarrierOperation operation in Enum.GetValues<BarrierOperation>())
            {
                data.Add(scenario, operation);
            }
        }

        return data;
    }

    /// <summary>Verifies each pair is accepted or rejected exactly as the oracle says and rejection changes nothing.</summary>
    /// <param name="scenario">Starting configuration.</param>
    /// <param name="operation">Attempted operation.</param>
    [Theory]
    [MemberData(nameof(Universe))]
    public async Task EveryScenarioAndOperation_IsAcceptedOrRejectedAsTheOracleSays(Scenario scenario, BarrierOperation operation)
    {
        Held held = await ReachAsync(scenario);
        MutationAdmissionBarrier barrier = held.Barrier;
        MutationAdmissionState before = barrier.State;
        bool expectedAccepted = Accepts(scenario, operation);

        Exception? failure = null;
        Task? pending = null;
        MutationAdmissionLease? lease = null;
        try
        {
            switch (operation)
            {
                case BarrierOperation.AcquireOrdinary:
                    lease = barrier.AcquireOrdinary();
                    break;
                case BarrierOperation.CloseDestructive:
                    (pending, lease) = await StartCloseAsync(barrier, MutationAdmissionClosure.Destructive);
                    break;
                case BarrierOperation.CloseShutdown:
                    (pending, lease) = await StartCloseAsync(barrier, MutationAdmissionClosure.Shutdown);
                    break;
                case BarrierOperation.EnterRecoveryOnly:
                    barrier.EnterRecoveryOnly();
                    break;
                case BarrierOperation.AcquireRecovery:
                    lease = await barrier.AcquireRecoveryAsync(CancellationToken.None);
                    break;
                case BarrierOperation.AcquireShutdownMaintenance:
                    lease = barrier.AcquireShutdownMaintenance();
                    break;
                case BarrierOperation.RequestRecoveryShutdown:
                    pending = barrier.RequestRecoveryShutdownAsync(CancellationToken.None);
                    break;
                default:
                    throw new NotSupportedException(operation.ToString());
            }
        }
        catch (MutationAdmissionRejectedException exception)
        {
            failure = exception;
            Assert.Equal(before, exception.State);
        }

        Assert.Equal(expectedAccepted, failure is null);
        if (!expectedAccepted)
        {
            Assert.Equal(before, barrier.State);
        }
        else
        {
            Assert.Equal(ExpectedStateAfter(scenario, operation), barrier.State);
        }

        lease?.Dispose();
        _ = pending;
        held.DisposeAll();
    }

    /// <summary>Verifies the oracle itself covers each declared admission state through some scenario.</summary>
    [Fact]
    public async Task Scenarios_ReachEveryDeclaredAdmissionState()
    {
        HashSet<MutationAdmissionState> reached = [];
        foreach (Scenario scenario in Enum.GetValues<Scenario>())
        {
            Held held = await ReachAsync(scenario);
            reached.Add(held.Barrier.State);
            held.DisposeAll();
        }

        Assert.Equal(
            Enum.GetValues<MutationAdmissionState>().Order(),
            reached.Order());
    }

    /// <summary>Verifies a destructive drain completes exactly when the last ordinary lease is released, in any release order.</summary>
    [Fact]
    public async Task Drain_CompletesExactlyWhenTheLastOrdinaryLeaseIsReleased()
    {
        Random random = new(SettingsGenerators.Seed);
        for (int round = 0; round < 50; round++)
        {
            MutationAdmissionBarrier barrier = new();
            List<MutationAdmissionLease> leases = [.. Enumerable.Range(0, 1 + random.Next(6)).Select(_ => barrier.AcquireOrdinary())];
            Task<MutationAdmissionLease> task = barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
            Assert.Equal(MutationAdmissionState.Closing, barrier.State);
            Assert.All(leases, lease => Assert.True(lease.RevocationToken.IsCancellationRequested));

            List<MutationAdmissionLease> releaseOrder = [.. leases.OrderBy(_ => random.Next())];
            foreach (MutationAdmissionLease lease in releaseOrder.Take(releaseOrder.Count - 1))
            {
                lease.Dispose();
                lease.Dispose();
                Assert.False(task.IsCompleted, "the drain must wait for every ordinary lease");
            }

            releaseOrder[^1].Dispose();
            MutationAdmissionLease exclusive = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(exclusive.IsExclusive);
            barrier.EnsureActiveExclusiveLease(exclusive);
            Assert.All(leases, lease => Assert.Throws<InvalidOperationException>(() => barrier.EnsureActiveLease(lease)));
            exclusive.Dispose();
            Assert.Equal(MutationAdmissionState.Open, barrier.State);
            using MutationAdmissionLease reopened = barrier.AcquireOrdinary();
            Assert.False(reopened.RevocationToken.IsCancellationRequested, "a new epoch must not inherit revocation");
        }
    }

    /// <summary>Verifies cancelling a pending drain reopens admission and leaves the held ordinary leases valid.</summary>
    [Fact]
    public async Task Drain_CancelledWhilePending_ReopensAndKeepsExistingLeasesValid()
    {
        MutationAdmissionBarrier barrier = new();
        MutationAdmissionLease ordinary = barrier.AcquireOrdinary();
        using CancellationTokenSource cancellation = new();
        Task<MutationAdmissionLease> drain = barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
        barrier.EnsureActiveLease(ordinary);
        using MutationAdmissionLease another = barrier.AcquireOrdinary();
        ordinary.Dispose();
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
    }

    /// <summary>Verifies the recovery-only retention lifecycle for ordinary and destructive owners, including shutdown commit.</summary>
    [Fact]
    public async Task RetainRecoveryOnly_FromEveryEligibleOwner_RequiresVerifiedRecoveryToReopen()
    {
        foreach (bool destructive in new[] { false, true })
        {
            MutationAdmissionBarrier barrier = new();
            MutationAdmissionLease owner = destructive
                ? await barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None)
                : barrier.AcquireOrdinary();

            Task retained = owner.RetainRecoveryOnlyAsync();
            await retained.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(MutationAdmissionState.RecoveryOnly, barrier.State);
            Assert.Throws<MutationAdmissionRejectedException>(() => barrier.AcquireOrdinary());
            using (MutationAdmissionLease failedAttempt = await barrier.AcquireRecoveryAsync(CancellationToken.None))
            {
                await Assert.ThrowsAsync<MutationAdmissionRejectedException>(async () => await barrier.AcquireRecoveryAsync(CancellationToken.None));
                failedAttempt.CompleteRecoveryAttempt(journalPresent: true, verifiedSuccess: false);
                Assert.Equal(MutationAdmissionState.RecoveryOnly, barrier.State);
                Assert.Throws<ObjectDisposedException>(() => failedAttempt.CommitShutdown());
            }

            MutationAdmissionLease incomplete = await barrier.AcquireRecoveryAsync(CancellationToken.None);
            incomplete.CompleteRecoveryAttempt(journalPresent: true, verifiedSuccess: true);
            Assert.Equal(MutationAdmissionState.RecoveryOnly, barrier.State);
            MutationAdmissionLease unverified = await barrier.AcquireRecoveryAsync(CancellationToken.None);
            unverified.CompleteRecoveryAttempt(journalPresent: false, verifiedSuccess: false);
            Assert.Equal(MutationAdmissionState.RecoveryOnly, barrier.State);
            MutationAdmissionLease verified = await barrier.AcquireRecoveryAsync(CancellationToken.None);
            verified.CompleteRecoveryAttempt(journalPresent: false, verifiedSuccess: true);
            Assert.Equal(MutationAdmissionState.Open, barrier.State);
            barrier.AcquireOrdinary().Dispose();
        }
    }

    /// <summary>Verifies a committed shutdown is absorbing whether it is committed by a drained lease or requested from recovery.</summary>
    [Fact]
    public async Task Shutdown_IsAbsorbingWhetherCommittedByTheLeaseOrRequestedFromRecovery()
    {
        MutationAdmissionBarrier committed = new();
        MutationAdmissionLease destructive = await committed.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        destructive.CommitShutdown();
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, committed.State);
        Assert.Throws<MutationAdmissionRejectedException>(() => committed.AcquireShutdownMaintenance());
        destructive.Dispose();
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, committed.State);
        await AssertTerminalAsync(committed);

        MutationAdmissionBarrier requested = new();
        MutationAdmissionLease owner = await requested.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await owner.RetainRecoveryOnlyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MutationAdmissionState.RecoveryOnly, requested.State);
        await requested.RequestRecoveryShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, requested.State);
        await AssertTerminalAsync(requested);
    }

    /// <summary>Verifies a shutdown requested while recovery runs waits for the attempt and then closes, regardless of its result.</summary>
    /// <param name="journalPresent">Whether the attempt reports a remaining journal.</param>
    /// <param name="verifiedSuccess">Whether the attempt reports verified success.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ShutdownRequestedDuringRecovery_ClosesAfterTheAttemptWhateverItsResult(bool journalPresent, bool verifiedSuccess)
    {
        MutationAdmissionBarrier barrier = new();
        barrier.EnterRecoveryOnly();
        MutationAdmissionLease recovery = await barrier.AcquireRecoveryAsync(CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        Task shutdown = barrier.RequestRecoveryShutdownAsync(cancellation.Token);
        Assert.Equal(MutationAdmissionState.RecoveryClosing, barrier.State);
        Assert.False(shutdown.IsCompleted);

        recovery.CompleteRecoveryAttempt(journalPresent, verifiedSuccess);

        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
        await AssertTerminalAsync(barrier);
    }

    /// <summary>Verifies a caller giving up on a recovery shutdown request cancels only its wait, not the shutdown.</summary>
    [Fact]
    public async Task RecoveryShutdownRequest_CancelledWait_DoesNotUndoTheClosing()
    {
        MutationAdmissionBarrier barrier = new();
        barrier.EnterRecoveryOnly();
        MutationAdmissionLease recovery = await barrier.AcquireRecoveryAsync(CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        Task shutdown = barrier.RequestRecoveryShutdownAsync(cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shutdown);

        Assert.Equal(MutationAdmissionState.RecoveryClosing, barrier.State);
        recovery.CompleteRecoveryAttempt(journalPresent: false, verifiedSuccess: true);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
    }

    /// <summary>Verifies completing a recovery attempt through a lease that is not a recovery lease is rejected without detaching it.</summary>
    [Fact]
    public async Task CompleteRecoveryAttempt_OnALeaseThatIsNotARecoveryLease_DoesNotLeakTheLease()
    {
        MutationAdmissionBarrier barrier = new();
        MutationAdmissionLease ordinary = barrier.AcquireOrdinary();

        Assert.Throws<InvalidOperationException>(() => ordinary.CompleteRecoveryAttempt(journalPresent: false, verifiedSuccess: true));

        barrier.EnsureActiveLease(ordinary);
        ordinary.Dispose();
        using MutationAdmissionLease drained = await barrier
            .CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(drained.IsExclusive);
    }

    /// <summary>Verifies the safety invariants over seeded random operation sequences, including illegal operations.</summary>
    /// <param name="seedOffset">Offset added to the fixed seed.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task RandomOperationSequences_PreserveTheSafetyInvariants(int seedOffset)
    {
        Random random = new(SettingsGenerators.Seed + seedOffset);
        HashSet<MutationAdmissionState> visited = [];
        for (int epoch = 0; epoch < 6; epoch++)
        {
            await WalkAsync(random, 120, visited);
        }

        Assert.Contains(MutationAdmissionState.Open, visited);
        Assert.Contains(MutationAdmissionState.Closing, visited);
        Assert.Contains(MutationAdmissionState.RecoveryOnly, visited);
        Assert.Contains(MutationAdmissionState.ClosedForShutdown, visited);
    }

    private static async Task WalkAsync(Random random, int steps, HashSet<MutationAdmissionState> visited)
    {
        MutationAdmissionBarrier barrier = new();
        List<MutationAdmissionLease> ordinary = [];
        List<MutationAdmissionLease> exclusive = [];
        HashSet<MutationAdmissionLease> recoveryLeases = [];
        List<(Task<MutationAdmissionLease> Task, CancellationTokenSource Source)> pendingDrains = [];
        bool shutdownSeen = false;

        for (int step = 0; step < steps; step++)
        {
            MutationAdmissionState before = barrier.State;
            switch (random.Next(14))
            {
                case 0 or 1:
                    TryAcquireOrdinary(barrier, ordinary, before);
                    break;
                case 2 when ordinary.Count > 0:
                    DisposeRandom(ordinary, random);
                    break;
                case 3:
                    CancellationTokenSource source = new();
                    try
                    {
                        Task<MutationAdmissionLease> drain = barrier
                            .CloseAndDrainAsync(random.Next(40) == 0 ? MutationAdmissionClosure.Shutdown : MutationAdmissionClosure.Destructive, source.Token)
                            .AsTask();
                        pendingDrains.Add((drain, source));
                    }
                    catch (MutationAdmissionRejectedException)
                    {
                        source.Dispose();
                    }

                    break;
                case 4 when pendingDrains.Count > 0:
                    (Task<MutationAdmissionLease> Task, CancellationTokenSource Source) victim = pendingDrains[random.Next(pendingDrains.Count)];
                    await victim.Source.CancelAsync();
                    break;
                case 5 when exclusive.Count > 0:
                    DisposeRandom(exclusive, random);
                    break;
                case 6:
                    try
                    {
                        barrier.EnterRecoveryOnly();
                    }
                    catch (MutationAdmissionRejectedException)
                    {
                    }

                    break;
                case 7:
                    try
                    {
                        MutationAdmissionLease acquired = await barrier.AcquireRecoveryAsync(CancellationToken.None);
                        exclusive.Add(acquired);
                        recoveryLeases.Add(acquired);
                    }
                    catch (MutationAdmissionRejectedException)
                    {
                    }

                    break;
                case 8:
                    try
                    {
                        exclusive.Add(barrier.AcquireShutdownMaintenance());
                    }
                    catch (MutationAdmissionRejectedException)
                    {
                    }

                    break;
                case 9 when recoveryLeases.Count > 0:
                    MutationAdmissionLease recovery = recoveryLeases.First();
                    recovery.CompleteRecoveryAttempt(random.Next(2) == 0, random.Next(2) == 0);
                    recoveryLeases.Remove(recovery);
                    exclusive.Remove(recovery);
                    break;
                case 10 when random.Next(8) == 0:
                    try
                    {
                        _ = barrier.RequestRecoveryShutdownAsync(CancellationToken.None);
                    }
                    catch (MutationAdmissionRejectedException)
                    {
                    }

                    break;
                case 11 when exclusive.Count > 0:
                    try
                    {
                        exclusive[random.Next(exclusive.Count)].CommitShutdown();
                    }
                    catch (Exception exception) when (exception is InvalidOperationException)
                    {
                    }

                    break;
                case 12 when ordinary.Count + exclusive.Count > 0:
                    List<MutationAdmissionLease> pool = random.Next(2) == 0 && ordinary.Count > 0 ? ordinary : exclusive.Count > 0 ? exclusive : ordinary;
                    MutationAdmissionLease retainer = pool[random.Next(pool.Count)];
                    Task retention = retainer.RetainRecoveryOnlyAsync();
                    if (!retention.IsFaulted)
                    {
                        pool.Remove(retainer);
                        recoveryLeases.Remove(retainer);
                    }
                    else
                    {
                        _ = retention.Exception;
                    }

                    break;
                default:
                    break;
            }

            await CollectDrainResultsAsync(pendingDrains, exclusive);
            visited.Add(barrier.State);
            AssertInvariants(barrier, ordinary, exclusive, ref shutdownSeen);
        }

        foreach (MutationAdmissionLease lease in ordinary.Concat(exclusive))
        {
            lease.Dispose();
        }
    }

    private static async Task AssertTerminalAsync(MutationAdmissionBarrier barrier)
    {
        Assert.Throws<MutationAdmissionRejectedException>(() => barrier.AcquireOrdinary());
        Assert.Throws<MutationAdmissionRejectedException>(() => barrier.EnterRecoveryOnly());
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None));
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(async () => await barrier.AcquireRecoveryAsync(CancellationToken.None));
        using MutationAdmissionLease maintenance = barrier.AcquireShutdownMaintenance();
        Assert.True(maintenance.IsExclusive);
        Assert.Throws<MutationAdmissionRejectedException>(() => barrier.AcquireShutdownMaintenance());
    }

    private static async Task CollectDrainResultsAsync(
        List<(Task<MutationAdmissionLease> Task, CancellationTokenSource Source)> pending,
        List<MutationAdmissionLease> exclusive)
    {
        foreach ((Task<MutationAdmissionLease> task, CancellationTokenSource source) in pending.ToArray())
        {
            if (!task.IsCompleted)
            {
                continue;
            }

            pending.RemoveAll(item => ReferenceEquals(item.Task, task));
            source.Dispose();
            if (task.IsCompletedSuccessfully)
            {
                exclusive.Add(await task);
            }
        }
    }

    private static void TryAcquireOrdinary(MutationAdmissionBarrier barrier, List<MutationAdmissionLease> ordinary, MutationAdmissionState before)
    {
        try
        {
            MutationAdmissionLease lease = barrier.AcquireOrdinary();
            Assert.Equal(MutationAdmissionState.Open, before);
            ordinary.Add(lease);
        }
        catch (MutationAdmissionRejectedException exception)
        {
            Assert.Equal(before, exception.State);
        }
    }

    private static void DisposeRandom(List<MutationAdmissionLease> leases, Random random)
    {
        MutationAdmissionLease lease = leases[random.Next(leases.Count)];
        leases.Remove(lease);
        lease.Dispose();
        lease.Dispose();
    }

    private static void AssertInvariants(
        MutationAdmissionBarrier barrier,
        List<MutationAdmissionLease> ordinary,
        List<MutationAdmissionLease> exclusive,
        ref bool shutdownSeen)
    {
        MutationAdmissionState state = barrier.State;
        if (shutdownSeen)
        {
            Assert.Equal(MutationAdmissionState.ClosedForShutdown, state);
        }

        shutdownSeen |= state == MutationAdmissionState.ClosedForShutdown;
        int activeExclusive = exclusive.Count(lease => IsActive(barrier, lease));
        int activeOrdinary = ordinary.Count(lease => IsActive(barrier, lease));
        Assert.True(activeExclusive <= 1, "at most one exclusive lease may be active");
        Assert.False(activeExclusive == 1 && ordinary.Count > 0, "an exclusive lease cannot coexist with undisposed ordinary leases");
        Assert.False(state == MutationAdmissionState.Open && activeExclusive > 0, "Open admission cannot have an exclusive owner");
        Assert.True(activeOrdinary == 0 || state is MutationAdmissionState.Open or MutationAdmissionState.Closing);
        if (state != MutationAdmissionState.Open)
        {
            Assert.All(ordinary, lease => Assert.True(lease.RevocationToken.IsCancellationRequested, "leaving Open must revoke ordinary leases"));
        }
        else
        {
            Assert.All(ordinary, lease => Assert.True(IsActive(barrier, lease)));
        }
    }

    private static bool IsActive(MutationAdmissionBarrier barrier, MutationAdmissionLease lease)
    {
        try
        {
            barrier.EnsureActiveLease(lease);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<(Task Task, MutationAdmissionLease? Lease)> StartCloseAsync(MutationAdmissionBarrier barrier, MutationAdmissionClosure closure)
    {
        Task<MutationAdmissionLease> close = barrier.CloseAndDrainAsync(closure, CancellationToken.None).AsTask();
        if (close.IsCompleted)
        {
            return (Task.CompletedTask, await close);
        }

        return (close, null);
    }

    private static bool Accepts(Scenario scenario, BarrierOperation operation) => (scenario, operation) switch
    {
        (Scenario.Open or Scenario.OpenWithOrdinary, BarrierOperation.AcquireOrdinary) => true,
        (Scenario.Open or Scenario.OpenWithOrdinary, BarrierOperation.CloseDestructive or BarrierOperation.CloseShutdown) => true,
        (Scenario.Open, BarrierOperation.EnterRecoveryOnly) => true,
        (Scenario.RecoveryOnly, BarrierOperation.AcquireRecovery) => true,
        (Scenario.ClosedForShutdown, BarrierOperation.AcquireShutdownMaintenance) => true,
        (Scenario.RecoveryOnly or Scenario.RecoveryOnlyExclusive or Scenario.RecoveryClosing or Scenario.ClosedForShutdown
            or Scenario.ClosedForShutdownExclusive, BarrierOperation.RequestRecoveryShutdown) => true,
        _ => false,
    };

    private static MutationAdmissionState ExpectedStateAfter(Scenario scenario, BarrierOperation operation) => (scenario, operation) switch
    {
        (_, BarrierOperation.AcquireOrdinary) => MutationAdmissionState.Open,
        (_, BarrierOperation.CloseDestructive) => MutationAdmissionState.Closing,
        (_, BarrierOperation.CloseShutdown) when scenario == Scenario.Open => MutationAdmissionState.ClosedForShutdown,
        (_, BarrierOperation.CloseShutdown) => MutationAdmissionState.Closing,
        (_, BarrierOperation.EnterRecoveryOnly) => MutationAdmissionState.RecoveryOnly,
        (_, BarrierOperation.AcquireRecovery) => MutationAdmissionState.RecoveryOnly,
        (_, BarrierOperation.AcquireShutdownMaintenance) => MutationAdmissionState.ClosedForShutdown,
        (Scenario.RecoveryOnly, BarrierOperation.RequestRecoveryShutdown) => MutationAdmissionState.ClosedForShutdown,
        (Scenario.RecoveryOnlyExclusive or Scenario.RecoveryClosing, BarrierOperation.RequestRecoveryShutdown) => MutationAdmissionState.RecoveryClosing,
        (_, BarrierOperation.RequestRecoveryShutdown) => MutationAdmissionState.ClosedForShutdown,
        _ => throw new NotSupportedException($"{scenario} x {operation}"),
    };

    private static async Task<Held> ReachAsync(Scenario scenario)
    {
        MutationAdmissionBarrier barrier = new();
        Held held = new(barrier);
        switch (scenario)
        {
            case Scenario.Open:
                break;
            case Scenario.OpenWithOrdinary:
                held.Leases.Add(barrier.AcquireOrdinary());
                break;
            case Scenario.ClosingDraining:
                held.Leases.Add(barrier.AcquireOrdinary());
                held.Tasks.Add(barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask());
                break;
            case Scenario.ClosingExclusive:
                held.Leases.Add(await barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None));
                break;
            case Scenario.RecoveryOnly:
                barrier.EnterRecoveryOnly();
                break;
            case Scenario.RecoveryOnlyExclusive:
                barrier.EnterRecoveryOnly();
                held.Leases.Add(await barrier.AcquireRecoveryAsync(CancellationToken.None));
                break;
            case Scenario.RecoveryClosing:
                barrier.EnterRecoveryOnly();
                held.Leases.Add(await barrier.AcquireRecoveryAsync(CancellationToken.None));
                held.Tasks.Add(barrier.RequestRecoveryShutdownAsync(CancellationToken.None));
                break;
            case Scenario.ClosedForShutdown:
                (await barrier.CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, CancellationToken.None)).Dispose();
                break;
            case Scenario.ClosedForShutdownExclusive:
                held.Leases.Add(await barrier.CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, CancellationToken.None));
                break;
            default:
                throw new NotSupportedException(scenario.ToString());
        }

        return held;
    }

    private sealed class Held(MutationAdmissionBarrier barrier)
    {
        public MutationAdmissionBarrier Barrier { get; } = barrier;

        public List<MutationAdmissionLease> Leases { get; } = [];

        public List<Task> Tasks { get; } = [];

        public void DisposeAll()
        {
            foreach (MutationAdmissionLease lease in Leases)
            {
                lease.Dispose();
            }
        }
    }
}
