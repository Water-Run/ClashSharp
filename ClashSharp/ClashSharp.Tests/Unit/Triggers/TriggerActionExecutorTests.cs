using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using ClashSharpMode = ClashSharp.Model.ClashSharpMode;

namespace ClashSharp.Tests.Unit.Triggers;

/// <summary>Verifies ordered durable action execution and recovery decisions.</summary>
public sealed class TriggerActionExecutorTests
{
    [Theory]
    [MemberData(nameof(CurrentActions))]
    public async Task ExecuteAsync_PendingActionCommitsRunningBeforeEffectAndVerifiedTerminalState(
        TriggerAction action,
        TriggerOutboxState expectedTerminalState)
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(execution, [Outbox(execution, 0, action)]);
        FakeTriggerActionRuntime runtime = expectedTerminalState == TriggerOutboxState.HandedOff
            ? new(
                [TriggerActionProbeResult.NotDesired()],
                [TriggerActionApplyResult.HandedOff()])
            : new(
                [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.Desired()],
                [TriggerActionApplyResult.Applied()]);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        IReadOnlyList<TriggerActionResult> results = await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None);

        Assert.Equal(expectedTerminalState, Assert.Single(results).FinalState);
        Assert.Equal(1, runtime.ApplyCount);
        Assert.Equal(
            [
                (TriggerOutboxState.Pending, TriggerOutboxState.Running),
                (TriggerOutboxState.Running, expectedTerminalState),
            ],
            repository.Transitions);
        Assert.Equal(repository.Actions[0].IdempotencyKey, Assert.Single(runtime.AppliedActions).IdempotencyKey);
    }

    [Theory]
    [MemberData(nameof(RepeatableActions))]
    public async Task ExecuteAsync_RunningDesiredActionReconcilesWithoutDuplicateEffect(
        TriggerAction action)
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, action, TriggerOutboxState.Running, attemptCount: 1)]);
        FakeTriggerActionRuntime runtime = new([TriggerActionProbeResult.Desired()], []);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        IReadOnlyList<TriggerActionResult> results = await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None);

        Assert.Equal(TriggerOutboxState.Succeeded, Assert.Single(results).FinalState);
        Assert.Equal(0, runtime.ApplyCount);
        Assert.Equal(
            [(TriggerOutboxState.Running, TriggerOutboxState.Succeeded)],
            repository.Transitions);
    }

    [Theory]
    [MemberData(nameof(RepeatableActions))]
    public async Task ExecuteAsync_RunningUnsatisfiedActionReturnsToPendingBeforeSafeRetry(
        TriggerAction action)
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, action, TriggerOutboxState.Running, attemptCount: 1)]);
        FakeTriggerActionRuntime runtime = new(
            [
                TriggerActionProbeResult.NotDesired(),
                TriggerActionProbeResult.NotDesired(),
                TriggerActionProbeResult.Desired(),
            ],
            [TriggerActionApplyResult.Applied()]);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        IReadOnlyList<TriggerActionResult> results = await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None);

        Assert.Equal(TriggerOutboxState.Succeeded, Assert.Single(results).FinalState);
        Assert.Equal(1, runtime.ApplyCount);
        Assert.Equal(
            [
                (TriggerOutboxState.Running, TriggerOutboxState.Pending),
                (TriggerOutboxState.Pending, TriggerOutboxState.Running),
                (TriggerOutboxState.Running, TriggerOutboxState.Succeeded),
            ],
            repository.Transitions);
    }

    [Theory]
    [MemberData(nameof(RepeatableActions))]
    public async Task ExecuteAsync_RunningUnknownActionBecomesUncertainWithoutReapplying(
        TriggerAction action)
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, action, TriggerOutboxState.Running, attemptCount: 1)]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.Unknown("trigger.action.probe_unavailable")],
            []);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        IReadOnlyList<TriggerActionResult> results = await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None);

        TriggerActionResult result = Assert.Single(results);
        Assert.Equal(TriggerOutboxState.Uncertain, result.FinalState);
        Assert.Equal("trigger.action.probe_unavailable", result.DiagnosticCode);
        Assert.Equal(0, runtime.ApplyCount);
        Assert.Equal(
            [(TriggerOutboxState.Running, TriggerOutboxState.Uncertain)],
            repository.Transitions);
    }

    [Fact]
    public async Task ExecuteAsync_ConclusiveFailureRemainsDiagnosableAndBlocksLaterActions()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [
                Outbox(execution, 0, BooleanAction(TriggerActionKind.SetConnectionSampling)),
                Outbox(execution, 1, NotificationAction()),
            ]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired()],
            [TriggerActionApplyResult.Failed("trigger.action.denied")]);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        IReadOnlyList<TriggerActionResult> results = await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None);

        TriggerActionResult result = Assert.Single(results);
        Assert.Equal(TriggerOutboxState.Failed, result.FinalState);
        Assert.Equal("trigger.action.denied", result.DiagnosticCode);
        Assert.Equal(TriggerOutboxState.Pending, repository.Actions[1].State);
        Assert.Equal(1, runtime.ApplyCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownVerificationBecomesUncertainAndBlocksLaterActions()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [
                Outbox(execution, 0, BooleanAction(TriggerActionKind.SetTransparentProxy)),
                Outbox(execution, 1, NotificationAction()),
            ]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.Unknown("probe.unavailable")],
            [TriggerActionApplyResult.Applied()]);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        IReadOnlyList<TriggerActionResult> results = await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None);

        TriggerActionResult result = Assert.Single(results);
        Assert.Equal(TriggerOutboxState.Uncertain, result.FinalState);
        Assert.Equal("probe.unavailable", result.DiagnosticCode);
        Assert.Equal(TriggerOutboxState.Pending, repository.Actions[1].State);
    }

    [Fact]
    public async Task ExecuteAsync_PendingDesiredActionStillCommitsOrderedDurableBoundariesWithoutDispatch()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, BooleanAction(TriggerActionKind.SetTransparentProxy))]);
        FakeTriggerActionRuntime runtime = new([TriggerActionProbeResult.Desired()], []);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        IReadOnlyList<TriggerActionResult> results = await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None);

        Assert.Equal(TriggerOutboxState.Succeeded, Assert.Single(results).FinalState);
        Assert.Equal(0, runtime.ApplyCount);
        Assert.Equal(
            [
                (TriggerOutboxState.Pending, TriggerOutboxState.Running),
                (TriggerOutboxState.Running, TriggerOutboxState.Succeeded),
            ],
            repository.Transitions);
    }

    [Fact]
    public async Task ExecuteAsync_AppliedButNotDesiredBecomesDiagnosableFailure()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, BooleanAction(TriggerActionKind.SetLaunchAtStartup))]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.NotDesired()],
            [TriggerActionApplyResult.Applied()]);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        TriggerActionResult result = Assert.Single(await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None));

        Assert.Equal(TriggerOutboxState.Failed, result.FinalState);
        Assert.Equal("trigger.action.verification_failed", result.DiagnosticCode);
    }

    [Theory]
    [MemberData(nameof(ExistingBlockingStates))]
    public async Task ExecuteAsync_ExistingBlockingStateStopsBeforeLaterPendingAction(
        int stateValue,
        string? diagnosticCode)
    {
        TriggerOutboxState state = (TriggerOutboxState)stateValue;
        TriggerExecution execution = Execution();
        TriggerAction blockingAction = state == TriggerOutboxState.HandedOff
            ? new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters())
            : NotificationAction();
        InMemoryTriggerRepository repository = new(
            execution,
            [
                Outbox(execution, 0, blockingAction, state, lastError: diagnosticCode),
                Outbox(execution, 1, NotificationAction()),
            ]);
        FakeTriggerActionRuntime runtime = state == TriggerOutboxState.HandedOff
            ? new(
                [TriggerActionProbeResult.NotDesired()],
                [TriggerActionApplyResult.HandedOff()])
            : new([], []);
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        TriggerActionResult result = Assert.Single(await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None));

        Assert.Equal(state, result.FinalState);
        Assert.Equal(diagnosticCode, result.DiagnosticCode);
        Assert.Equal(TriggerOutboxState.Pending, repository.Actions[1].State);
        Assert.Empty(repository.Transitions);
        Assert.Equal(state == TriggerOutboxState.HandedOff ? 1 : 0, runtime.ApplyCount);
    }

    [Fact]
    public async Task ExecuteAsync_NotifiesTheCommittedExecutionBeforeApplyingItsFirstAction()
    {
        List<string> timeline = [];
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, BooleanAction(TriggerActionKind.SetTransparentProxy))]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.Desired()],
            [TriggerActionApplyResult.Applied()],
            timeline);
        RecordingFiredNotificationSink notifications = new(timeline);
        TriggerActionExecutor executor = new(repository, runtime, notifications);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        await executor.ExecuteAsync(execution, lease, CancellationToken.None);

        Assert.Same(execution, Assert.Single(notifications.Executions));
        Assert.Equal(["notify", "apply"], timeline);
    }

    [Fact]
    public async Task ReconcileAsync_NotifiesTheDurableExecutionBeforeRecoveringItsActions()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, BooleanAction(TriggerActionKind.SetTransparentProxy))]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.Desired()],
            [TriggerActionApplyResult.Applied()]);
        RecordingFiredNotificationSink notifications = new();
        TriggerActionExecutor executor = new(repository, runtime, notifications);
        MutationAdmissionBarrier admission = new();
        TriggerActionReconciler reconciler = new(repository, executor, admission);

        await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Same(execution, Assert.Single(notifications.Executions));
    }

    [Fact]
    public async Task ExecuteAsync_FiredNotificationFailureDoesNotBlockBusinessActions()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, BooleanAction(TriggerActionKind.SetTransparentProxy))]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.Desired()],
            [TriggerActionApplyResult.Applied()]);
        ThrowingFiredNotificationSink notifications = new();
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            notifications);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        IReadOnlyList<TriggerActionResult> results = await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None);

        Assert.Equal(TriggerOutboxState.Succeeded, Assert.Single(results).FinalState);
        Assert.Equal(1, runtime.ApplyCount);
        Assert.IsType<InvalidOperationException>(Assert.Single(notifications.Failures));
    }

    [Fact]
    public async Task ExecuteAsync_FiredNotificationWrapsFatalFailure_PropagatesBeforeBusinessActions()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, BooleanAction(TriggerActionKind.SetTransparentProxy))]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.Desired()],
            [TriggerActionApplyResult.Applied()]);
        InvalidOperationException failure = new(
            "notification wrapper",
            CreateProcessFatalException<OutOfMemoryException>());
        ThrowingFiredNotificationSink notifications = new()
        {
            NotifyException = failure,
        };
        TriggerActionExecutor executor = new(repository, runtime, notifications);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(execution, lease, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(0, runtime.ApplyCount);
        Assert.Empty(notifications.Failures);
    }

    [Fact]
    public async Task ExecuteAsync_NotificationFailureReportingFailsFatally_Propagates()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, BooleanAction(TriggerActionKind.SetTransparentProxy))]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.Desired()],
            [TriggerActionApplyResult.Applied()]);
        InvalidOperationException reportFailure = new(
            "report wrapper",
            CreateProcessFatalException<AccessViolationException>());
        ThrowingFiredNotificationSink notifications = new()
        {
            ReportException = reportFailure,
        };
        TriggerActionExecutor executor = new(repository, runtime, notifications);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(execution, lease, CancellationToken.None));

        Assert.Same(reportFailure, actual);
        Assert.Equal(0, runtime.ApplyCount);
        Assert.Single(notifications.Failures);
    }

    [Fact]
    public async Task ReconcileAsync_FiredNotificationFailureDoesNotBlockRecovery()
    {
        TriggerExecution execution = Execution();
        InMemoryTriggerRepository repository = new(
            execution,
            [Outbox(execution, 0, BooleanAction(TriggerActionKind.SetTransparentProxy))]);
        FakeTriggerActionRuntime runtime = new(
            [TriggerActionProbeResult.NotDesired(), TriggerActionProbeResult.Desired()],
            [TriggerActionApplyResult.Applied()]);
        ThrowingFiredNotificationSink notifications = new();
        TriggerActionExecutor executor = new(
            repository,
            runtime,
            notifications);
        MutationAdmissionBarrier admission = new();
        TriggerActionReconciler reconciler = new(repository, executor, admission);

        IReadOnlyList<TriggerActionResult> results = await reconciler.ReconcileAsync(
            CancellationToken.None);

        Assert.Equal(TriggerOutboxState.Succeeded, Assert.Single(results).FinalState);
        Assert.Equal(1, runtime.ApplyCount);
        Assert.IsType<InvalidOperationException>(Assert.Single(notifications.Failures));
    }

    public static TheoryData<TriggerAction, TriggerOutboxState> CurrentActions => new()
    {
        { new TriggerAction(TriggerActionKind.CloseConnections, new NoActionParameters()), TriggerOutboxState.Succeeded },
        { BooleanAction(TriggerActionKind.SetLaunchAtStartup), TriggerOutboxState.Succeeded },
        { BooleanAction(TriggerActionKind.SetTransparentProxy), TriggerOutboxState.Succeeded },
        { BooleanAction(TriggerActionKind.SetConnectionSampling), TriggerOutboxState.Succeeded },
        {
            new TriggerAction(
                TriggerActionKind.SwitchProxyMode,
                new ProxyModeActionParameters(ClashSharpMode.RuleTakeover)),
            TriggerOutboxState.Succeeded
        },
        { NotificationAction(), TriggerOutboxState.Succeeded },
        { new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters()), TriggerOutboxState.HandedOff },
    };

    public static TheoryData<TriggerAction> RepeatableActions => new()
    {
        new TriggerAction(TriggerActionKind.CloseConnections, new NoActionParameters()),
        BooleanAction(TriggerActionKind.SetLaunchAtStartup),
        BooleanAction(TriggerActionKind.SetTransparentProxy),
        BooleanAction(TriggerActionKind.SetConnectionSampling),
        new TriggerAction(
            TriggerActionKind.SwitchProxyMode,
            new ProxyModeActionParameters(ClashSharpMode.RuleTakeover)),
        NotificationAction(),
    };

    public static TheoryData<int, string?> ExistingBlockingStates => new()
    {
        { (int)TriggerOutboxState.Failed, "trigger.action.failed.test" },
        { (int)TriggerOutboxState.Uncertain, "trigger.action.uncertain.test" },
        { (int)TriggerOutboxState.HandedOff, null },
    };

    private static TriggerAction BooleanAction(TriggerActionKind kind)
    {
        return new TriggerAction(kind, new BooleanActionParameters(true));
    }

    private static TriggerAction NotificationAction()
    {
        return new TriggerAction(
            TriggerActionKind.SendNotification,
            new NotificationActionParameters("done"));
    }

    private static TriggerExecution Execution()
    {
        return new TriggerExecution(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "task",
            1,
            DateTimeOffset.UnixEpoch,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TriggerExecutionState.Pending);
    }

    private static TriggerOutboxAction Outbox(
        TriggerExecution execution,
        int index,
        TriggerAction action,
        TriggerOutboxState state = TriggerOutboxState.Pending,
        int attemptCount = 0,
        string? lastError = null)
    {
        return new TriggerOutboxAction(
            execution.ExecutionId,
            execution.TaskRevision,
            index,
            TriggerIdempotencyKey.Create(execution.ExecutionId, execution.TaskRevision, index),
            action,
            state,
            attemptCount,
            lastError);
    }

    private sealed class FakeTriggerActionRuntime(
        IEnumerable<TriggerActionProbeResult> probes,
        IEnumerable<TriggerActionApplyResult> applies,
        List<string>? timeline = null)
        : ITriggerActionRuntime
    {
        private readonly Queue<TriggerActionProbeResult> _probes = new(probes);
        private readonly Queue<TriggerActionApplyResult> _applies = new(applies);

        public List<TriggerOutboxAction> AppliedActions { get; } = [];

        public int ApplyCount => AppliedActions.Count;

        public Task<TriggerActionProbeResult> ProbeAsync(
            TriggerOutboxAction action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_probes.Dequeue());
        }

        public Task<TriggerActionApplyResult> ApplyAsync(
            TriggerOutboxAction action,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline?.Add("apply");
            AppliedActions.Add(action);
            return Task.FromResult(_applies.Dequeue());
        }
    }

    private sealed class RecordingFiredNotificationSink(List<string>? timeline = null)
        : ITriggerFiredNotificationSink
    {
        public List<TriggerExecution> Executions { get; } = [];

        public Task NotifyAsync(
            TriggerExecution execution,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline?.Add("notify");
            Executions.Add(execution);
            return Task.CompletedTask;
        }

        public void ReportFailure(TriggerExecution execution, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ArgumentNullException.ThrowIfNull(exception);
        }
    }

    private sealed class ThrowingFiredNotificationSink : ITriggerFiredNotificationSink
    {
        public List<Exception> Failures { get; } = [];

        public Exception NotifyException { get; init; } =
            new InvalidOperationException("notification unavailable");

        public Exception? ReportException { get; init; }

        public Task NotifyAsync(
            TriggerExecution execution,
            CancellationToken cancellationToken) =>
            Task.FromException(NotifyException);

        public void ReportFailure(TriggerExecution execution, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(execution);
            Failures.Add(exception);
            if (ReportException is not null)
            {
                throw ReportException;
            }
        }
    }

    private sealed class InMemoryTriggerRepository : ITriggerRepository
    {
        private readonly TriggerExecution _execution;

        public InMemoryTriggerRepository(
            TriggerExecution execution,
            IEnumerable<TriggerOutboxAction> actions)
        {
            _execution = execution;
            Actions = [.. actions];
        }

        public List<TriggerOutboxAction> Actions { get; }

        public List<(TriggerOutboxState Expected, TriggerOutboxState Next)> Transitions { get; } = [];

        public Task<TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>>> ReadExecutionActionsAsync(
            Guid executionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(TriggerPersistenceResult.Succeeded<IReadOnlyList<TriggerOutboxAction>>(
                Actions.ToArray()));
        }

        public Task<TriggerPersistenceResult<TriggerOutboxAction>> TransitionOutboxAsync(
            TriggerOutboxTransition transition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Actions.FindIndex(action =>
                action.ExecutionId == transition.ExecutionId
                && action.ActionIndex == transition.ActionIndex);
            if (index < 0)
            {
                return Task.FromResult(TriggerPersistenceResult.NotFound<TriggerOutboxAction>());
            }

            TriggerOutboxAction current = Actions[index];
            if (current.State != transition.ExpectedState)
            {
                return Task.FromResult(TriggerPersistenceResult.Conflict<TriggerOutboxAction>());
            }

            int attempts = current.AttemptCount
                + (transition.NextState == TriggerOutboxState.Running ? 1 : 0);
            TriggerOutboxAction updated = new(
                current.ExecutionId,
                current.TaskRevision,
                current.ActionIndex,
                current.IdempotencyKey,
                current.DesiredEffect,
                transition.NextState,
                attempts,
                transition.LastError);
            Actions[index] = updated;
            Transitions.Add((transition.ExpectedState, transition.NextState));
            return Task.FromResult(TriggerPersistenceResult.Succeeded(updated));
        }

        public Task<TriggerPersistenceResult<TriggerRepositorySnapshot>> OpenAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TriggerPersistenceResult<TriggerRepositorySnapshot>> ReadSnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TriggerPersistenceResult> ReplaceDefinitionsAsync(
            TriggerDefinitionWriteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TriggerPersistenceResult> TryImportMigrationAsync(
            TriggerMigrationImportRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TriggerPersistenceResult> TryCommitStateAsync(
            TriggerStateCommitRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TriggerPersistenceResult<TriggerExecution>> TryCommitExecutionAsync(
            TriggerExecutionCommitRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>>> ReadRecoverableActionsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(TriggerPersistenceResult.Succeeded<IReadOnlyList<TriggerOutboxAction>>(
                Actions.ToArray()));
        }

        public Task<TriggerPersistenceResult<TriggerExecution>> ReadExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(executionId == _execution.ExecutionId
                ? TriggerPersistenceResult.Succeeded(_execution)
                : TriggerPersistenceResult.NotFound<TriggerExecution>());
        }

        public Task<TriggerPersistenceResult<TriggerLifecycleHandoff>> ReadLifecycleHandoffAsync(
            Guid executionId,
            int actionIndex,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TriggerPersistenceResult<TriggerLifecycleHandoff>> TransitionLifecycleHandoffAsync(
            TriggerLifecycleHandoffTransition transition,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TriggerPersistenceResult> CreateBackupAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static TException CreateProcessFatalException<TException>()
        where TException : Exception =>
        Activator.CreateInstance<TException>();
}
