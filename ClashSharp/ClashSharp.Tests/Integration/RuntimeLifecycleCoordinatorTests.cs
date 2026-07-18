using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies ordered, recoverable shutdown preparation before host disposal.</summary>
public sealed class RuntimeLifecycleCoordinatorTests
{
    [Fact]
    public async Task ShutdownAsync_OpenAdmission_QuiescesMutatesAndStopsInOrder()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        FakeParticipant first = new("first", trace, wasRunning: true);
        FakeParticipant second = new("second", trace, wasRunning: false);
        FakeNetworkShutdown network = new(trace);
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(barrier, network, first, second);

        RuntimeShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, result.Outcome);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
        Assert.Equal(
            ["first.quiesce", "second.quiesce", "network.shutdown", "second.stop", "first.stop"],
            trace);
    }

    [Fact]
    public async Task ShutdownAsync_WaitsForAdmittedWorkBeforeQuiescing()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        MutationAdmissionLease inFlight = await barrier.AcquireOrdinaryAsync(CancellationToken.None);
        FakeParticipant participant = new("producer", trace, wasRunning: true);
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(
            barrier,
            new FakeNetworkShutdown(trace),
            participant);

        Task<RuntimeShutdownResult> shutdownTask = coordinator.ShutdownAsync(CancellationToken.None);
        await WaitUntilAsync(() => barrier.State == MutationAdmissionState.Closing);

        Assert.Empty(trace);
        await inFlight.DisposeAsync();
        RuntimeShutdownResult result = await shutdownTask;

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, result.Outcome);
        Assert.Equal("producer.quiesce", trace[0]);
    }

    [Fact]
    public async Task ShutdownAsync_ExistingDestructiveOperationFinishesThenShutdownRetriesAdmission()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        MutationAdmissionLease destructive = await barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            CancellationToken.None);
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(
            barrier,
            new FakeNetworkShutdown(trace),
            new FakeParticipant("producer", trace, wasRunning: true));

        Task<RuntimeShutdownResult> shutdownTask = coordinator.ShutdownAsync(CancellationToken.None);
        await Task.Delay(30);
        Assert.False(shutdownTask.IsCompleted);

        await destructive.DisposeAsync();
        RuntimeShutdownResult result = await shutdownTask;

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, result.Outcome);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
    }

    [Fact]
    public async Task ShutdownAsync_RevokesActionWaitingBeforeMutationGateThenQuiesces()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        MutationAdmissionLease waitingAction = await barrier.AcquireOrdinaryAsync(CancellationToken.None);
        Task actionTask = Task.Run(async () =>
        {
            await WaitForCancellationAsync(waitingAction.RevocationToken);
            trace.Add("action.released");
            await waitingAction.DisposeAsync();
        });
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(
            barrier,
            new FakeNetworkShutdown(trace),
            new FakeParticipant("trigger", trace, wasRunning: true));

        RuntimeShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);
        await actionTask;

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, result.Outcome);
        Assert.True(trace.IndexOf("action.released") < trace.IndexOf("trigger.quiesce"));
    }

    [Fact]
    public async Task ShutdownAsync_QuiescenceTimesOut_ResumesPausedParticipantsInReverse()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        FakeParticipant first = new("first", trace, wasRunning: true);
        FakeParticipant blocked = new("blocked", trace, wasRunning: true)
        {
            BlockQuiescenceUntilCancellation = true,
        };
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(
            barrier,
            new FakeNetworkShutdown(trace),
            TimeSpan.FromMilliseconds(50),
            first,
            blocked);

        RuntimeShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(RuntimeShutdownOutcome.Aborted, result.Outcome);
        Assert.Equal("quiescence-timeout", result.ErrorCode);
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
        Assert.Equal(["first.quiesce", "blocked.quiesce", "first.resume"], trace);
    }

    [Fact]
    public async Task ShutdownAsync_ResumeFails_ReturnsTypedDegradedOutcomeAndReopensAdmission()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        FakeParticipant first = new("first", trace, wasRunning: true)
        {
            ResumeException = new InvalidOperationException("resume failed"),
        };
        FakeParticipant blocked = new("blocked", trace, wasRunning: true)
        {
            BlockQuiescenceUntilCancellation = true,
        };
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(
            barrier,
            new FakeNetworkShutdown(trace),
            TimeSpan.FromMilliseconds(50),
            first,
            blocked);

        RuntimeShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(RuntimeShutdownOutcome.Degraded, result.Outcome);
        Assert.Equal("quiescence-restore-failed", result.ErrorCode);
        Assert.Equal(["first"], result.DegradedParticipants);
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
    }

    [Fact]
    public async Task ShutdownAsync_RecoveryOnly_SkipsCompetingNetworkMutation()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        barrier.EnterRecoveryOnly();
        FakeNetworkShutdown network = new(trace);
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(
            barrier,
            network,
            new FakeParticipant("trigger", trace, wasRunning: true));

        RuntimeShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, result.Outcome);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
        Assert.Equal(0, network.CallCount);
        Assert.Equal(["trigger.quiesce", "trigger.stop"], trace);
    }

    [Fact]
    public async Task ShutdownAsync_CommittedNetworkRecovery_RemainsPreparedAndPreservesJournalOutcome()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        FakeNetworkShutdown network = new(trace)
        {
            Outcome = MutationOutcome.CommittedRecoveryRequired,
            ErrorCode = "shutdown-cleanup-recovery-required",
        };
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(
            barrier,
            network,
            new FakeParticipant("trigger", trace, wasRunning: true));

        RuntimeShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, result.Outcome);
        Assert.Equal("shutdown-cleanup-recovery-required", result.ErrorCode);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
        Assert.Equal(["trigger.quiesce", "network.shutdown", "trigger.stop"], trace);
    }

    [Fact]
    public async Task ShutdownAsync_UncommittedNetworkFailure_ResumesAndReopens()
    {
        List<string> trace = [];
        MutationAdmissionBarrier barrier = new();
        FakeNetworkShutdown network = new(trace)
        {
            Outcome = MutationOutcome.Compensated,
            ErrorCode = "shutdown-network-compensated",
        };
        RuntimeLifecycleCoordinator coordinator = CreateCoordinator(
            barrier,
            network,
            new FakeParticipant("trigger", trace, wasRunning: true));

        RuntimeShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(RuntimeShutdownOutcome.Aborted, result.Outcome);
        Assert.Equal("shutdown-network-compensated", result.ErrorCode);
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
        Assert.Equal(["trigger.quiesce", "network.shutdown", "trigger.resume"], trace);
    }

    private static RuntimeLifecycleCoordinator CreateCoordinator(
        MutationAdmissionBarrier barrier,
        IRuntimeShutdownNetworkCoordinator network,
        params IRuntimeParticipant[] participants)
    {
        return CreateCoordinator(barrier, network, TimeSpan.FromSeconds(30), participants);
    }

    private static RuntimeLifecycleCoordinator CreateCoordinator(
        MutationAdmissionBarrier barrier,
        IRuntimeShutdownNetworkCoordinator network,
        TimeSpan timeout,
        params IRuntimeParticipant[] participants)
    {
        return new RuntimeLifecycleCoordinator(
            barrier,
            network,
            () => new NetworkIntent(NetworkIntentKind.Shutdown, ClashSharpMode.Disabled, false, 7890),
            participants,
            timeout);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<object?> signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => signal.TrySetResult(null));
        return signal.Task;
    }

    private sealed class FakeNetworkShutdown(List<string> trace) : IRuntimeShutdownNetworkCoordinator
    {
        public int CallCount { get; private set; }

        public MutationOutcome Outcome { get; init; } = MutationOutcome.Succeeded;

        public string? ErrorCode { get; init; }

        public Task<MutationResult<NetworkTransitionResult>> ApplyShutdownAsync(
            NetworkIntent intent,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            trace.Add("network.shutdown");
            NetworkTransitionResult state = new(
                ClashSharpMode.Disabled,
                false,
                false,
                false,
                7890,
                "disabled");
            return Task.FromResult(new MutationResult<NetworkTransitionResult>(
                Guid.NewGuid(),
                Outcome,
                state,
                ErrorCode));
        }
    }

    private sealed class FakeParticipant(
        string name,
        List<string> trace,
        bool wasRunning) : IRuntimeParticipant
    {
        public string Name => name;

        public bool BlockQuiescenceUntilCancellation { get; init; }

        public Exception? ResumeException { get; init; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"{name}.start");
            return Task.CompletedTask;
        }

        public async Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
        {
            trace.Add($"{name}.quiesce");
            if (BlockQuiescenceUntilCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new QuiescedState(wasRunning);
        }

        public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"{name}.resume");
            return ResumeException is null ? Task.CompletedTask : Task.FromException(ResumeException);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"{name}.stop");
            return Task.CompletedTask;
        }
    }
}
