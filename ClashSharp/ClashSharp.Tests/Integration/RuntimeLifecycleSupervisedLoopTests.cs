using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Supervision;

namespace ClashSharp.Tests.Integration;

public sealed class RuntimeLifecycleSupervisedLoopTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task InterruptedQuiescenceRestoresTheAffectedProducerOrReportsItsRecoveryFailure(
        bool callerCancels, bool releaseBeforeRecoveryEnds)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int activeIterations = 0;
        int overlappingIterations = 0;
        var loop = new SupervisedLoop(
            "subscriptions",
            async token =>
            {
                int active = Interlocked.Increment(ref activeIterations);
                if (active != 1) { Interlocked.Increment(ref overlappingIterations); }
                try
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        using CancellationTokenRegistration registration = token.Register(() => cancellationReached.TrySetResult());
                        started.TrySetResult();
                        // Models an accepted request that needs time to finish cancellation.
                        await release.Task;
                    }
                    else
                    {
                        resumed.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                }
                finally { Interlocked.Decrement(ref activeIterations); }
            },
            () => TimeSpan.FromHours(1),
            SystemSupervisorClock.Instance,
            new SupervisorBackoffPolicy(() => 0d));
        var participant = new ObservedParticipant(loop);
        var registry = new RuntimeLifetimeRegistry();
        registry.RegisterParticipant(participant);
        var barrier = new MutationAdmissionBarrier();
        var coordinator = new RuntimeLifecycleCoordinator(barrier, registry, TimeSpan.FromSeconds(1),
            RuntimeShutdownNetworkPolicy.PreserveCurrentState);
        using var cancellation = new CancellationTokenSource();
        Task<RuntimeShutdownResult>? shutdown = null;
        try
        {
            await loop.StartAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = coordinator.ShutdownAsync(cancellation.Token);
            await cancellationReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (callerCancels) { cancellation.Cancel(); }
            await participant.Interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Only release after Quiesce has actually failed, avoiding a completion/cancellation race.
            if (releaseBeforeRecoveryEnds) { release.TrySetResult(); }
            RuntimeShutdownResult result = await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, participant.ResumeCalls);
            Assert.True(participant.ResumedPriorState!.WasRunning);
            Assert.Equal(MutationAdmissionState.Open, barrier.State);
            if (releaseBeforeRecoveryEnds)
            {
                Assert.Equal(RuntimeShutdownOutcome.Aborted, result.Outcome);
                Assert.Equal(callerCancels ? "shutdown-cancelled" : "quiescence-timeout", result.ErrorCode);
                Assert.Empty(result.DegradedParticipants);
                await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(loop.IsRunning);
                Assert.Equal(2, Volatile.Read(ref calls));
            }
            else
            {
                Assert.Equal(RuntimeShutdownOutcome.Degraded, result.Outcome);
                Assert.Equal("quiescence-restore-failed", result.ErrorCode);
                Assert.Equal(["subscriptions"], result.DegradedParticipants);
            }
            Assert.Equal(0, overlappingIterations);
        }
        finally
        {
            release.TrySetResult();
            cancellation.Cancel();
            if (shutdown is not null) { await shutdown.WaitAsync(TimeSpan.FromSeconds(5)); }
            await loop.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedParticipantIsCompensatedFirstAndDoesNotPreventEarlierRecovery(bool recoveryFails)
    {
        List<string> trace = [];
        var registry = new RuntimeLifetimeRegistry();
        registry.RegisterParticipant(new RecordedParticipant("first", trace));
        registry.RegisterParticipant(new RecordedParticipant("interrupted", trace, interrupt: true, recoveryFails));
        registry.RegisterParticipant(new RecordedParticipant("never-started", trace));
        var coordinator = new RuntimeLifecycleCoordinator(new MutationAdmissionBarrier(), registry,
            networkPolicy: RuntimeShutdownNetworkPolicy.PreserveCurrentState);

        RuntimeShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(["first.quiesce", "interrupted.quiesce", "interrupted.resume", "first.resume"], trace);
        Assert.Equal(recoveryFails ? RuntimeShutdownOutcome.Degraded : RuntimeShutdownOutcome.Aborted, result.Outcome);
        Assert.Equal(recoveryFails ? "quiescence-restore-failed" : "quiescence-failed", result.ErrorCode);
        Assert.Equal(recoveryFails ? ["interrupted"] : Array.Empty<string>(), result.DegradedParticipants);
    }

    private sealed class RecordedParticipant(string name, List<string> trace, bool interrupt = false, bool recoveryFails = false) : IRuntimeParticipant
    {
        public string Name => name;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
        {
            trace.Add(name + ".quiesce");
            QuiescedState state = new(true);
            return interrupt ? Task.FromException<QuiescedState>(new RuntimeQuiescenceException(state, new IOException("Pause failed.")))
                : Task.FromResult(state);
        }
        public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
        {
            Assert.True(priorState.WasRunning);
            trace.Add(name + ".resume");
            return recoveryFails ? Task.FromException(new IOException("Recovery failed.")) : Task.CompletedTask;
        }
    }

    private sealed class ObservedParticipant(SupervisedLoop loop) : IRuntimeParticipant
    {
        internal TaskCompletionSource Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ResumeCalls { get; private set; }
        internal QuiescedState? ResumedPriorState { get; private set; }
        public string Name => loop.Name;
        public Task StartAsync(CancellationToken cancellationToken) => loop.StartAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => loop.StopAsync(cancellationToken);

        public async Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
        {
            try { return await loop.QuiesceAsync(cancellationToken); }
            catch
            {
                Interrupted.TrySetResult();
                throw;
            }
        }

        public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
        {
            ResumeCalls++;
            ResumedPriorState = priorState;
            return loop.ResumeAsync(priorState, cancellationToken);
        }
    }
}
