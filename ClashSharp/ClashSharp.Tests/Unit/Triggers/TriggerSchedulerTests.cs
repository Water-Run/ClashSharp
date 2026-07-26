using System.Collections.Concurrent;
using System.Threading.Channels;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;

namespace ClashSharp.Tests.Unit.Triggers;

/// <summary>Verifies deterministic event ownership and lifecycle barriers in the trigger scheduler.</summary>
public sealed class TriggerSchedulerTests
{
    [Fact]
    public async Task DisabledScheduler_DrainsTicksAndEventsWithoutRequestingEvaluation()
    {
        FakeSchedulerSettings settings = new() { IsEnabled = false };
        FakeSchedulerEventSource events = new();
        FakeSchedulerClock clock = new();
        RecordingEvaluator evaluator = new();
        TriggerScheduler scheduler = CreateScheduler(settings, events, clock, evaluator);
        await scheduler.StartAsync(CancellationToken.None);
        await clock.WaitUntilWaitingAsync(1);

        events.Publish(TriggerEventKind.AppEntered);
        await clock.TickAndWaitForNextAsync();
        QuiescedState prior = await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.True(prior.WasRunning);
        Assert.Empty(evaluator.Events);
        Assert.Equal(SupervisorHealthState.Stopped, scheduler.Health.State);
    }

    [Fact]
    public async Task PeriodicTick_IsAwaitedAndEvaluatedDeterministically()
    {
        FakeSchedulerEventSource events = new();
        FakeSchedulerClock clock = new();
        RecordingEvaluator evaluator = new();
        TriggerScheduler scheduler = CreateScheduler(new FakeSchedulerSettings(), events, clock, evaluator);
        await scheduler.StartAsync(CancellationToken.None);
        await clock.WaitUntilWaitingAsync(1);

        await clock.TickAndWaitForNextAsync();
        TriggerSchedulerEvent evaluated = await evaluator.ReadCallAsync();
        await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.Equal(TriggerEventKind.Periodic, evaluated.EventKind);
        Assert.Null(evaluated.NotificationLevel);
    }

    [Fact]
    public async Task RepeatedTicks_DoNotLeaveWaitersThatCaptureTheNextRuntimeWakeSignal()
    {
        FakeSchedulerEventSource events = new();
        FakeSchedulerClock clock = new();
        RecordingEvaluator evaluator = new();
        TriggerScheduler scheduler = CreateScheduler(new FakeSchedulerSettings(), events, clock, evaluator);
        await scheduler.StartAsync(CancellationToken.None);
        await clock.WaitUntilWaitingAsync(1);

        for (int index = 0; index < 8; index++)
        {
            await clock.TickAndWaitForNextAsync();
            Assert.Equal(TriggerEventKind.Periodic, (await evaluator.ReadCallAsync()).EventKind);
        }

        events.Publish(TriggerEventKind.ProxyStarted);
        Assert.Equal(TriggerEventKind.ProxyStarted, (await evaluator.ReadCallAsync()).EventKind);
        await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.Equal(9, evaluator.Events.Count);
    }

    [Fact]
    public async Task RuntimeEvents_ArrivingDuringEvaluation_AreRetainedInPublicationOrder()
    {
        TaskCompletionSource<object?> releaseFirst = Signal();
        FakeSchedulerEventSource events = new();
        RecordingEvaluator evaluator = new()
        {
            Handler = async (_, index, _) =>
            {
                if (index == 0)
                {
                    await releaseFirst.Task;
                }

                return TriggerSchedulerEvaluationOutcome.Succeeded();
            },
        };
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator);
        await scheduler.StartAsync(CancellationToken.None);

        events.Publish(TriggerEventKind.AppEntered);
        Assert.Equal(TriggerEventKind.AppEntered, (await evaluator.ReadCallAsync()).EventKind);
        for (int index = 0; index < 64; index++)
        {
            events.Publish(
                TriggerEventKind.NotificationRaised,
                index % 2 == 0
                    ? TriggerNotificationLevel.Default
                    : TriggerNotificationLevel.CriticalOnly);
        }

        releaseFirst.TrySetResult(null);
        QuiescedState prior = await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.True(prior.WasRunning);
        Assert.Equal(65, evaluator.Events.Count);
        Assert.Equal(TriggerEventKind.AppEntered, evaluator.Events[0].EventKind);
        Assert.Equal(
            Enumerable.Range(0, 64).Select(index => index % 2 == 0
                ? TriggerNotificationLevel.Default
                : TriggerNotificationLevel.CriticalOnly),
            evaluator.Events.Skip(1).Select(static item => item.NotificationLevel!.Value));
    }

    [Fact]
    public async Task EvaluationFailure_BecomesHealthAndDoesNotTerminateOwnedLoop()
    {
        List<SupervisorHealth> observedHealth = [];
        RecordingEvaluator evaluator = new()
        {
            Handler = (_, index, _) => index == 0
                ? Task.FromException<TriggerSchedulerEvaluationOutcome>(new IOException("storage unavailable"))
                : Task.FromResult(TriggerSchedulerEvaluationOutcome.Succeeded()),
        };
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator,
            observedHealth.Add);
        await scheduler.StartAsync(CancellationToken.None);

        events.Publish(TriggerEventKind.AppEntered);
        events.Publish(TriggerEventKind.ProxyStarted);
        await evaluator.ReadCallAsync();
        await evaluator.ReadCallAsync();
        await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.Equal(2, evaluator.Events.Count);
        Assert.Contains(observedHealth, health =>
            health.State == SupervisorHealthState.Retrying
            && health.ErrorCode == "supervisor.io");
    }

    [Fact]
    public async Task ClockFailure_BecomesHealthAndTheOwnedLoopReestablishesItsWait()
    {
        List<SupervisorHealth> observedHealth = [];
        FakeSchedulerClock clock = new() { FailuresRemaining = 1 };
        FakeSchedulerEventSource events = new();
        RecordingEvaluator evaluator = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            clock,
            evaluator,
            observedHealth.Add);
        await scheduler.StartAsync(CancellationToken.None);
        await clock.WaitUntilWaitingAsync(2);

        events.Publish(TriggerEventKind.AppEntered);
        Assert.Equal(TriggerEventKind.AppEntered, (await evaluator.ReadCallAsync()).EventKind);
        await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.Contains(observedHealth, health =>
            health.State == SupervisorHealthState.Retrying
            && health.ErrorCode == "supervisor.io");
    }

    [Fact]
    public async Task SettingsFailure_BecomesHealthAndDoesNotTerminateTheOwnedLoop()
    {
        List<SupervisorHealth> observedHealth = [];
        FakeSchedulerSettings settings = new() { ReadFailure = new IOException("settings unavailable") };
        FakeSchedulerEventSource events = new();
        RecordingEvaluator evaluator = new();
        TriggerScheduler scheduler = CreateScheduler(
            settings,
            events,
            new FakeSchedulerClock(),
            evaluator,
            observedHealth.Add);
        await scheduler.StartAsync(CancellationToken.None);

        events.Publish(TriggerEventKind.AppEntered);
        await settings.WaitForReadAsync();
        settings.ReadFailure = null;
        events.Publish(TriggerEventKind.ProxyStarted);
        Assert.Equal(TriggerEventKind.ProxyStarted, (await evaluator.ReadCallAsync()).EventKind);
        await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.Contains(observedHealth, health =>
            health.State == SupervisorHealthState.Retrying
            && health.ErrorCode == "supervisor.io");
    }

    [Fact]
    public async Task CompletedEvaluation_AcknowledgesExitAfterEvaluatorLeaseUnwinds()
    {
        TriggerExecution execution = new(
            Guid.NewGuid(),
            "exit-task",
            1,
            DateTimeOffset.UnixEpoch,
            Guid.NewGuid(),
            TriggerExecutionState.HandedOff);
        TaskCompletionSource<object?> releaseEvaluation = Signal();
        RecordingEvaluator evaluator = new()
        {
            Handler = async (_, _, _) =>
            {
                await releaseEvaluation.Task;
                return TriggerSchedulerEvaluationOutcome.Succeeded([execution]);
            },
        };
        RecordingLifecycleHandoff handoff = new();
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator,
            lifecycleHandoff: handoff);
        await scheduler.StartAsync(CancellationToken.None);
        events.Publish(TriggerEventKind.AppEntered);
        await evaluator.ReadCallAsync();

        Assert.Empty(handoff.AcknowledgedExecutions);
        releaseEvaluation.TrySetResult(null);
        TriggerExecution acknowledged = await handoff.ReadAcknowledgementAsync();
        await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.Same(execution, acknowledged);
        Assert.Equal([execution.ExecutionId], handoff.AcknowledgedExecutions.Select(static item => item.ExecutionId));
    }

    [Fact]
    public async Task TransientReleaseAcknowledgementFailure_IsRetriedBeforeProcessingCompletes()
    {
        TriggerExecution execution = new(
            Guid.NewGuid(),
            "exit-task",
            1,
            DateTimeOffset.UnixEpoch,
            Guid.NewGuid(),
            TriggerExecutionState.HandedOff);
        RecordingEvaluator evaluator = new()
        {
            Handler = (_, _, _) => Task.FromResult(
                TriggerSchedulerEvaluationOutcome.Succeeded([execution])),
        };
        RecordingLifecycleHandoff handoff = new() { FailuresRemaining = 1 };
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator,
            lifecycleHandoff: handoff);
        await scheduler.StartAsync(CancellationToken.None);

        events.Publish(TriggerEventKind.AppEntered);
        TriggerExecution acknowledged = await handoff.ReadAcknowledgementAsync();
        await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.Same(execution, acknowledged);
        Assert.Equal(2, handoff.AttemptCount);
        Assert.Equal([execution.ExecutionId], handoff.AcknowledgedExecutions.Select(static item => item.ExecutionId));
    }

    [Fact]
    public async Task ExhaustedReleaseAcknowledgement_IsRetainedForTheNextAcceptedEvent()
    {
        TriggerExecution execution = new(
            Guid.NewGuid(),
            "exit-task",
            1,
            DateTimeOffset.UnixEpoch,
            Guid.NewGuid(),
            TriggerExecutionState.HandedOff);
        RecordingEvaluator evaluator = new()
        {
            Handler = (_, index, _) => Task.FromResult(index == 0
                ? TriggerSchedulerEvaluationOutcome.Succeeded([execution])
                : TriggerSchedulerEvaluationOutcome.Succeeded()),
        };
        RecordingLifecycleHandoff handoff = new() { FailuresRemaining = 3 };
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator,
            lifecycleHandoff: handoff);
        await scheduler.StartAsync(CancellationToken.None);

        events.Publish(TriggerEventKind.AppEntered);
        await evaluator.ReadCallAsync();
        events.Publish(TriggerEventKind.ProxyStarted);
        TriggerExecution acknowledged = await handoff.ReadAcknowledgementAsync();
        await scheduler.QuiesceAsync(CancellationToken.None);

        Assert.Same(execution, acknowledged);
        Assert.Equal(4, handoff.AttemptCount);
        Assert.Equal(2, evaluator.Events.Count);
    }

    [Fact]
    public async Task Quiesce_RejectsNewEventsAwaitsInflightAndResumeRestoresRunningState()
    {
        TaskCompletionSource<object?> releaseFirst = Signal();
        RecordingEvaluator evaluator = new()
        {
            Handler = async (_, index, _) =>
            {
                if (index == 0)
                {
                    await releaseFirst.Task;
                }

                return TriggerSchedulerEvaluationOutcome.Succeeded();
            },
        };
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator);
        await scheduler.StartAsync(CancellationToken.None);
        events.Publish(TriggerEventKind.AppEntered);
        await evaluator.ReadCallAsync();

        Task<QuiescedState> quiescing = scheduler.QuiesceAsync(CancellationToken.None);
        Assert.False(scheduler.IsAcceptingEvents);
        Assert.False(quiescing.IsCompleted);
        events.Publish(TriggerEventKind.ProxyStarted);
        releaseFirst.TrySetResult(null);
        QuiescedState prior = await quiescing;

        Assert.True(prior.WasRunning);
        Assert.Single(evaluator.Events);
        await scheduler.ResumeAsync(prior, CancellationToken.None);
        await scheduler.ResumeAsync(prior, CancellationToken.None);
        Assert.True(scheduler.IsAcceptingEvents);
        events.Publish(TriggerEventKind.ProxyStarted);
        Assert.Equal(TriggerEventKind.ProxyStarted, (await evaluator.ReadCallAsync()).EventKind);
        await scheduler.QuiesceAsync(CancellationToken.None);
        Assert.Equal(2, evaluator.Events.Count);
    }

    [Fact]
    public async Task CancelledQuiesce_RestoresPublicationWhileInflightWorkContinues()
    {
        TaskCompletionSource<object?> releaseFirst = Signal();
        RecordingEvaluator evaluator = new()
        {
            Handler = async (_, index, _) =>
            {
                if (index == 0)
                {
                    await releaseFirst.Task;
                }

                return TriggerSchedulerEvaluationOutcome.Succeeded();
            },
        };
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator);
        await scheduler.StartAsync(CancellationToken.None);
        events.Publish(TriggerEventKind.AppEntered);
        await evaluator.ReadCallAsync();
        using CancellationTokenSource cancellation = new();

        Task<QuiescedState> quiescing = scheduler.QuiesceAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quiescing);

        Assert.True(scheduler.IsAcceptingEvents);
        Assert.Equal(1, events.SubscriberCount);
        events.Publish(TriggerEventKind.ProxyStarted);
        releaseFirst.TrySetResult(null);
        Assert.Equal(TriggerEventKind.ProxyStarted, (await evaluator.ReadCallAsync()).EventKind);
        await scheduler.QuiesceAsync(CancellationToken.None);
        Assert.Equal(2, evaluator.Events.Count);
    }

    [Fact]
    public async Task CancelledQuiesce_RestoresTheHealthThatPrecededTheBarrier()
    {
        TaskCompletionSource<object?> retryingObserved = Signal();
        TaskCompletionSource<object?> releaseSecond = Signal();
        RecordingEvaluator evaluator = new()
        {
            Handler = (_, index, _) => index == 0
                ? Task.FromException<TriggerSchedulerEvaluationOutcome>(new IOException("storage unavailable"))
                : AwaitReleaseAsync(releaseSecond.Task),
        };
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator,
            health =>
            {
                if (health.State == SupervisorHealthState.Retrying)
                {
                    retryingObserved.TrySetResult(null);
                }
            });
        await scheduler.StartAsync(CancellationToken.None);
        events.Publish(TriggerEventKind.AppEntered);
        await retryingObserved.Task;
        events.Publish(TriggerEventKind.ProxyStarted);
        await evaluator.ReadCallAsync();
        await evaluator.ReadCallAsync();
        using CancellationTokenSource cancellation = new();

        Task<QuiescedState> quiescing = scheduler.QuiesceAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quiescing);

        Assert.Equal(SupervisorHealthState.Retrying, scheduler.Health.State);
        Assert.Equal("supervisor.io", scheduler.Health.ErrorCode);
        releaseSecond.TrySetResult(null);
        await scheduler.QuiesceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_CancelsSchedulingAndAwaitsTheSoleOwnedTask()
    {
        TaskCompletionSource<object?> releaseFirst = Signal();
        RecordingEvaluator evaluator = new()
        {
            Handler = async (_, _, _) =>
            {
                await releaseFirst.Task;
                return TriggerSchedulerEvaluationOutcome.Succeeded();
            },
        };
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator);
        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.StartAsync(CancellationToken.None);
        Assert.Equal(1, events.SubscriberCount);
        events.Publish(TriggerEventKind.AppEntered);
        await evaluator.ReadCallAsync();

        Task stopping = scheduler.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        Assert.False(scheduler.IsAcceptingEvents);
        releaseFirst.TrySetResult(null);
        await stopping;

        Assert.False(scheduler.IsRunning);
        Assert.Equal(0, events.SubscriberCount);
        await scheduler.StopAsync(CancellationToken.None);
        await scheduler.StartAsync(CancellationToken.None);
        events.Publish(TriggerEventKind.ProxyStarted);
        Assert.Single(evaluator.Events);
    }

    [Fact]
    public async Task Stop_TreatsOwnedWorkCancellationAsACompletedShutdown()
    {
        TaskCompletionSource<object?> neverCompletes = Signal();
        RecordingEvaluator evaluator = new()
        {
            Handler = async (_, _, cancellationToken) =>
            {
                await neverCompletes.Task.WaitAsync(cancellationToken);
                return TriggerSchedulerEvaluationOutcome.Succeeded();
            },
        };
        FakeSchedulerEventSource events = new();
        TriggerScheduler scheduler = CreateScheduler(
            new FakeSchedulerSettings(),
            events,
            new FakeSchedulerClock(),
            evaluator);
        await scheduler.StartAsync(CancellationToken.None);
        events.Publish(TriggerEventKind.AppEntered);
        await evaluator.ReadCallAsync();

        await scheduler.StopAsync(CancellationToken.None);

        Assert.False(scheduler.IsRunning);
        Assert.False(scheduler.IsAcceptingEvents);
        Assert.Equal(SupervisorHealthState.Stopped, scheduler.Health.State);
    }

    private static TriggerScheduler CreateScheduler(
        FakeSchedulerSettings settings,
        FakeSchedulerEventSource events,
        FakeSchedulerClock clock,
        RecordingEvaluator evaluator,
        Action<SupervisorHealth>? healthChanged = null,
        RecordingLifecycleHandoff? lifecycleHandoff = null)
    {
        return new TriggerScheduler(
            settings,
            events,
            clock,
            evaluator,
            lifecycleHandoff ?? new RecordingLifecycleHandoff(),
            healthChanged);
    }

    private static TaskCompletionSource<object?> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<TriggerSchedulerEvaluationOutcome> AwaitReleaseAsync(Task release)
    {
        await release;
        return TriggerSchedulerEvaluationOutcome.Succeeded();
    }

    private sealed class FakeSchedulerSettings : ITriggerSchedulerSettings
    {
        private bool _isEnabled = true;
        private readonly TaskCompletionSource<object?> _readObserved = Signal();

        public Exception? ReadFailure { get; set; }

        public bool IsEnabled
        {
            get
            {
                Exception? failure = ReadFailure;
                _readObserved.TrySetResult(null);
                return failure is null ? _isEnabled : throw failure;
            }

            set => _isEnabled = value;
        }

        public Task WaitForReadAsync() => _readObserved.Task;
    }

    private sealed class FakeSchedulerEventSource : ITriggerSchedulerEventSource
    {
        private EventHandler<TriggerSchedulerEvent>? _eventRaised;

        public int SubscriberCount => _eventRaised?.GetInvocationList().Length ?? 0;

        public event EventHandler<TriggerSchedulerEvent>? EventRaised
        {
            add => _eventRaised += value;
            remove => _eventRaised -= value;
        }

        public void Publish(
            TriggerEventKind eventKind,
            TriggerNotificationLevel? notificationLevel = null)
        {
            _eventRaised?.Invoke(this, new TriggerSchedulerEvent(eventKind, notificationLevel));
        }
    }

    private sealed class FakeSchedulerClock : ITriggerSchedulerClock
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<object?>> _waiters = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<object?>> _registrations = new();
        private int _waitCount;
        private int _releasedCount;

        public int FailuresRemaining { get; init; }

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddSeconds(Volatile.Read(ref _releasedCount));

        public Task WaitForNextTickAsync(CancellationToken cancellationToken)
        {
            int ordinal = Interlocked.Increment(ref _waitCount);
            _registrations.GetOrAdd(ordinal, static _ => Signal()).TrySetResult(null);
            if (ordinal <= FailuresRemaining)
            {
                return Task.FromException(new IOException("clock unavailable"));
            }

            TaskCompletionSource<object?> waiter = Signal();
            _waiters[ordinal] = waiter;
            return waiter.Task.WaitAsync(cancellationToken);
        }

        public Task WaitUntilWaitingAsync(int ordinal)
        {
            if (Volatile.Read(ref _waitCount) >= ordinal)
            {
                return Task.CompletedTask;
            }

            return _registrations.GetOrAdd(ordinal, static _ => Signal()).Task;
        }

        public async Task TickAndWaitForNextAsync()
        {
            int ordinal = Interlocked.Increment(ref _releasedCount);
            await WaitUntilWaitingAsync(ordinal);
            _waiters[ordinal].TrySetResult(null);
            await WaitUntilWaitingAsync(ordinal + 1);
        }
    }

    private sealed class RecordingEvaluator : ITriggerSchedulerEvaluator
    {
        private readonly Channel<TriggerSchedulerEvent> _calls = Channel.CreateUnbounded<TriggerSchedulerEvent>();
        private readonly object _syncLock = new();
        private readonly List<TriggerSchedulerEvent> _events = [];
        private int _callIndex;

        public Func<
            TriggerSchedulerEvent,
            int,
            CancellationToken,
            Task<TriggerSchedulerEvaluationOutcome>>? Handler
        {
            get;
            init;
        }

        public IReadOnlyList<TriggerSchedulerEvent> Events
        {
            get
            {
                lock (_syncLock)
                {
                    return _events.ToArray();
                }
            }
        }

        public Task<TriggerSchedulerEvaluationOutcome> EvaluateAsync(
            TriggerSchedulerEvent triggerEvent,
            CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _callIndex) - 1;
            lock (_syncLock)
            {
                _events.Add(triggerEvent);
            }

            _calls.Writer.TryWrite(triggerEvent);
            return Handler?.Invoke(triggerEvent, index, cancellationToken)
                ?? Task.FromResult(TriggerSchedulerEvaluationOutcome.Succeeded());
        }

        public ValueTask<TriggerSchedulerEvent> ReadCallAsync() =>
            _calls.Reader.ReadAsync(CancellationToken.None);
    }

    private sealed class RecordingLifecycleHandoff : ITriggerLifecycleHandoff
    {
        private readonly Channel<TriggerExecution> _acknowledgements =
            Channel.CreateUnbounded<TriggerExecution>();

        public List<TriggerExecution> AcknowledgedExecutions { get; } = [];

        public int FailuresRemaining { get; init; }

        public int AttemptCount { get; private set; }

        public Task<TriggerActionProbeResult> ProbeAsync(
            TriggerOutboxAction action,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TriggerActionApplyResult> HandOffAsync(
            TriggerOutboxAction action,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AcknowledgeReleaseAsync(
            TriggerLifecycleHandoffIdentity identity,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AcknowledgeReleasedExecutionAsync(
            TriggerExecution execution,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttemptCount++;
            if (AttemptCount <= FailuresRemaining)
            {
                throw new IOException("release acknowledgement unavailable");
            }

            AcknowledgedExecutions.Add(execution);
            _acknowledgements.Writer.TryWrite(execution);
            return Task.CompletedTask;
        }

        public ValueTask<TriggerExecution> ReadAcknowledgementAsync() =>
            _acknowledgements.Reader.ReadAsync(CancellationToken.None);
    }
}
