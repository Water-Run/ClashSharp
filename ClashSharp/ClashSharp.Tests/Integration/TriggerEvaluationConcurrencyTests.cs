using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model.Triggers;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies serialized evaluation and atomic state/outbox persistence against real SQLite.</summary>
public sealed class TriggerEvaluationConcurrencyTests
{
    [Fact]
    public async Task EvaluateAsync_ConcurrentEventsForOneTaskCreateOneExecution()
    {
        using TriggerTestDirectory directory = new();
        SqliteTriggerRepository repository = await CreateRepositoryAsync(directory, [Definition("task")]);
        TaskCompletionSource<object?> firstEntered = Signal();
        TaskCompletionSource<object?> releaseFirst = Signal();
        BarrierContextProvider provider = new(async (call, request, cancellationToken) =>
        {
            if (call == 1)
            {
                firstEntered.TrySetResult(null);
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return Available(request, activeConnections: 10);
        });
        BlockingDispatcher dispatcher = new();
        TriggerExecutionCoordinator coordinator = Coordinator(repository, provider, dispatcher: dispatcher);

        Task<TriggerEvaluationResult> periodic = coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);
        await firstEntered.Task;
        Task<TriggerEvaluationResult> runtime = coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.AppEntered,
            null,
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        releaseFirst.SetResult(null);
        await dispatcher.Entered;
        Assert.Equal(1, provider.CallCount);
        Assert.Single(await ReadOutboxAsync(repository));
        dispatcher.Release();
        TriggerEvaluationResult[] results = await Task.WhenAll(periodic, runtime);
        IReadOnlyList<TriggerOutboxAction> outbox = await ReadOutboxAsync(repository);

        Assert.Equal(1, provider.MaxConcurrentCalls);
        Assert.Single(results, result => result.Status == TriggerEvaluationStatus.Committed);
        Assert.Single(results, result => result.Status == TriggerEvaluationStatus.NotMatched);
        Assert.Single(outbox);
    }

    [Fact]
    public async Task EvaluateAsync_DifferentTasksCanAcquireContextConcurrently()
    {
        using TriggerTestDirectory directory = new();
        SqliteTriggerRepository repository = await CreateRepositoryAsync(
            directory,
            [Definition("first"), Definition("second")]);
        TaskCompletionSource<object?> firstEntered = Signal();
        TaskCompletionSource<object?> secondEntered = Signal();
        TaskCompletionSource<object?> release = Signal();
        BarrierContextProvider provider = new(async (call, request, cancellationToken) =>
        {
            (call == 1 ? firstEntered : secondEntered).TrySetResult(null);
            await release.Task.WaitAsync(cancellationToken);
            return Available(request, activeConnections: 10);
        });
        TriggerExecutionCoordinator coordinator = Coordinator(repository, provider);

        Task<TriggerEvaluationResult> first = coordinator.EvaluateAsync(
            "first",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);
        await firstEntered.Task;
        Task<TriggerEvaluationResult> second = coordinator.EvaluateAsync(
            "second",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);
        await secondEntered.Task;

        Assert.Equal(2, provider.MaxConcurrentCalls);
        release.SetResult(null);
        TriggerEvaluationResult[] results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Equal(TriggerEvaluationStatus.Committed, result.Status));
        Assert.Equal(2, (await ReadOutboxAsync(repository)).Count);
    }

    [Fact]
    public async Task EvaluateAsync_ObservedFalseRearmIsPersistedBeforeLaterTrueEvent()
    {
        using TriggerTestDirectory directory = new();
        SqliteTriggerRepository repository = await CreateRepositoryAsync(directory, [Definition("task")]);
        TaskCompletionSource<object?> falseEntered = Signal();
        TaskCompletionSource<object?> releaseFalse = Signal();
        BarrierContextProvider provider = new(async (call, request, cancellationToken) =>
        {
            if (call == 2)
            {
                falseEntered.TrySetResult(null);
                await releaseFalse.Task.WaitAsync(cancellationToken);
            }

            return Available(request, activeConnections: call == 2 ? 0 : 10);
        });
        TriggerExecutionCoordinator coordinator = Coordinator(repository, provider);
        TriggerEvaluationResult first = await coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);

        Task<TriggerEvaluationResult> observedFalse = coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);
        await falseEntered.Task;
        Task<TriggerEvaluationResult> observedTrue = coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.AppEntered,
            null,
            CancellationToken.None);
        Assert.Equal(2, provider.CallCount);
        releaseFalse.SetResult(null);

        TriggerEvaluationResult[] later = await Task.WhenAll(observedFalse, observedTrue);
        TriggerTaskState state = (await ReadSnapshotAsync(repository)).Tasks[0].State;

        Assert.Equal(TriggerEvaluationStatus.Committed, first.Status);
        Assert.Equal(TriggerEvaluationStatus.NotMatched, later[0].Status);
        Assert.Equal(TriggerEvaluationStatus.Committed, later[1].Status);
        Assert.Equal(3, state.Version);
        Assert.False(state.ConditionStates["connections"].IsArmed);
        Assert.Equal(2, (await ReadOutboxAsync(repository)).Count);
    }

    [Fact]
    public async Task EvaluateAsync_DefinitionConflictReloadsAndDoesNotCommitStaleMatch()
    {
        using TriggerTestDirectory directory = new();
        TriggerTaskDefinition original = Definition("task", revision: 1, threshold: 5);
        SqliteTriggerRepository repository = await CreateRepositoryAsync(directory, [original]);
        TaskCompletionSource<object?> firstEntered = Signal();
        TaskCompletionSource<object?> releaseFirst = Signal();
        BarrierContextProvider provider = new(async (call, request, cancellationToken) =>
        {
            if (call == 1)
            {
                firstEntered.TrySetResult(null);
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return Available(request, activeConnections: 50);
        });
        TriggerExecutionCoordinator coordinator = Coordinator(repository, provider);

        Task<TriggerEvaluationResult> evaluation = coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);
        await firstEntered.Task;
        TriggerPersistenceResult replacement = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(
                1,
                [Definition("task", revision: 2, threshold: 100)]),
            CancellationToken.None);
        releaseFirst.SetResult(null);

        TriggerEvaluationResult result = await evaluation;
        TriggerRepositorySnapshot snapshot = await ReadSnapshotAsync(repository);

        Assert.True(replacement.IsSucceeded, replacement.Diagnostic?.Code);
        Assert.Equal(TriggerEvaluationStatus.NotMatched, result.Status);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(2, snapshot.Tasks[0].Definition.Revision);
        Assert.Equal(0, snapshot.Tasks[0].State.Version);
        Assert.Empty(await ReadOutboxAsync(repository));
    }

    [Fact]
    public async Task EvaluateAsync_ClosedMutationAdmissionLeavesCommittedOutboxPending()
    {
        using TriggerTestDirectory directory = new();
        SqliteTriggerRepository repository = await CreateRepositoryAsync(directory, [Definition("task")]);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease destructive = await admission.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            CancellationToken.None);
        RecordingDispatcher dispatcher = new();
        TriggerExecutionCoordinator coordinator = Coordinator(
            repository,
            new BarrierContextProvider((_, request, _) =>
                Task.FromResult(Available(request, activeConnections: 10))),
            admission,
            dispatcher);

        TriggerEvaluationResult result = await coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);

        Assert.Equal(TriggerEvaluationStatus.Committed, result.Status);
        Assert.Equal(TriggerDispatchStatus.Deferred, result.DispatchStatus);
        Assert.Equal("trigger.dispatch.admission_closed", result.DiagnosticCode);
        Assert.Equal(0, dispatcher.CallCount);
        Assert.Single(await ReadOutboxAsync(repository));
    }

    [Fact]
    public async Task EvaluateAsync_DispatchCancellationWrapsFatalFailure_Propagates()
    {
        using TriggerTestDirectory directory = new();
        SqliteTriggerRepository repository = await CreateRepositoryAsync(
            directory,
            [Definition("task")]);
        OperationCanceledException failure = new(
            "dispatch cancelled while fatally failing",
            Activator.CreateInstance<OutOfMemoryException>(),
            CancellationToken.None);
        RecordingDispatcher dispatcher = new()
        {
            ExceptionToThrow = failure,
        };
        TriggerExecutionCoordinator coordinator = Coordinator(
            repository,
            new BarrierContextProvider((_, request, _) =>
                Task.FromResult(Available(request, activeConnections: 10))),
            dispatcher: dispatcher);

        OperationCanceledException actual =
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => coordinator.EvaluateAsync(
                    "task",
                    TriggerEventKind.Periodic,
                    null,
                    CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(1, dispatcher.CallCount);
        Assert.Single(await ReadOutboxAsync(repository));
    }

    [Fact]
    public async Task EvaluateAsync_DisabledTaskRequestsNoContextOrDispatch()
    {
        using TriggerTestDirectory directory = new();
        SqliteTriggerRepository repository = await CreateRepositoryAsync(
            directory,
            [Definition("task", isEnabled: false)]);
        BarrierContextProvider provider = new((_, request, _) =>
            Task.FromResult(Available(request, activeConnections: 10)));
        RecordingDispatcher dispatcher = new();
        TriggerExecutionCoordinator coordinator = Coordinator(
            repository,
            provider,
            dispatcher: dispatcher);

        TriggerEvaluationResult result = await coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);

        Assert.Equal(TriggerEvaluationStatus.Disabled, result.Status);
        Assert.Equal(TriggerDispatchStatus.NotRequested, result.DispatchStatus);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, dispatcher.CallCount);
        Assert.Empty(await ReadOutboxAsync(repository));
    }

    [Fact]
    public async Task EvaluateAsync_AdmissionDrainRevokesDispatchAndAwaitsTaskGateRelease()
    {
        using TriggerTestDirectory directory = new();
        SqliteTriggerRepository repository = await CreateRepositoryAsync(directory, [Definition("task")]);
        MutationAdmissionBarrier admission = new();
        BlockingDispatcher dispatcher = new();
        TriggerExecutionCoordinator coordinator = Coordinator(
            repository,
            new BarrierContextProvider((_, request, _) =>
                Task.FromResult(Available(request, activeConnections: 10))),
            admission,
            dispatcher);
        Task<TriggerEvaluationResult> evaluation = coordinator.EvaluateAsync(
            "task",
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);
        await dispatcher.Entered;

        Task<MutationAdmissionLease> drain = admission
            .CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None)
            .AsTask();
        TriggerEvaluationResult result = await evaluation;
        await using MutationAdmissionLease destructive = await drain;

        Assert.Equal(TriggerEvaluationStatus.Committed, result.Status);
        Assert.Equal(TriggerDispatchStatus.Deferred, result.DispatchStatus);
        Assert.Equal("trigger.dispatch.cancelled", result.DiagnosticCode);
        Assert.Single(await ReadOutboxAsync(repository));
    }

    private static TriggerExecutionCoordinator Coordinator(
        ITriggerRepository repository,
        ITriggerContextProvider provider,
        MutationAdmissionBarrier? admission = null,
        ITriggerExecutionDispatcher? dispatcher = null)
    {
        return new TriggerExecutionCoordinator(
            repository,
            new TriggerExecutionGate(),
            new TriggerEvaluator(provider),
            admission ?? new MutationAdmissionBarrier(),
            dispatcher ?? new RecordingDispatcher(),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    private static async Task<SqliteTriggerRepository> CreateRepositoryAsync(
        TriggerTestDirectory directory,
        IReadOnlyList<TriggerTaskDefinition> definitions)
    {
        SqliteTriggerRepository repository = new(directory.DatabasePath);
        TriggerPersistenceResult<TriggerRepositorySnapshot> opened = await repository.OpenAsync(
            CancellationToken.None);
        Assert.True(opened.IsSucceeded, opened.Diagnostic?.Code);
        TriggerPersistenceResult replaced = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(0, definitions),
            CancellationToken.None);
        Assert.True(replaced.IsSucceeded, replaced.Diagnostic?.Code);
        return repository;
    }

    private static async Task<TriggerRepositorySnapshot> ReadSnapshotAsync(
        ITriggerRepository repository)
    {
        TriggerPersistenceResult<TriggerRepositorySnapshot> result =
            await repository.ReadSnapshotAsync(CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsType<TriggerRepositorySnapshot>(result.Value);
    }

    private static async Task<IReadOnlyList<TriggerOutboxAction>> ReadOutboxAsync(
        ITriggerRepository repository)
    {
        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> result =
            await repository.ReadRecoverableActionsAsync(CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsAssignableFrom<IReadOnlyList<TriggerOutboxAction>>(result.Value);
    }

    private static TriggerTaskDefinition Definition(
        string id,
        long revision = 1,
        int threshold = 5,
        bool isEnabled = true)
    {
        return new TriggerTaskDefinition(
            id,
            revision,
            id,
            isEnabled,
            [
                new TriggerCondition(
                    "connections",
                    TriggerConditionKind.ActiveConnections,
                    new ActiveConnectionsConditionParameters(threshold)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters(id)),
            ]);
    }

    private static TriggerContextResult Available(
        TriggerContextRequest request,
        int activeConnections)
    {
        return TriggerContextResult.Available(new TriggerEvaluationContext(
            request.EventKind,
            new DateOnly(2026, 7, 23),
            new TimeOnly(10, 0),
            activeConnectionCount: activeConnections,
            notificationLevel: request.NotificationLevel));
    }

    private static TaskCompletionSource<object?> Signal()
    {
        return new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BarrierContextProvider(
        Func<int, TriggerContextRequest, CancellationToken, Task<TriggerContextResult>> acquire)
        : ITriggerContextProvider
    {
        private int _activeCalls;
        private int _callCount;
        private int _maxConcurrentCalls;

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public async Task<TriggerContextResult> AcquireAsync(
            TriggerContextRequest request,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            int active = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(ref _maxConcurrentCalls, active);
            try
            {
                return await acquire(call, request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            int current;
            do
            {
                current = Volatile.Read(ref maximum);
                if (candidate <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref maximum, candidate, current) != current);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingDispatcher : ITriggerExecutionDispatcher
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Exception? ExceptionToThrow { get; init; }

        public Task DispatchAsync(
            TriggerExecution execution,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return ExceptionToThrow is null
                ? Task.CompletedTask
                : Task.FromException(ExceptionToThrow);
        }
    }

    private sealed class BlockingDispatcher : ITriggerExecutionDispatcher
    {
        private readonly TaskCompletionSource<object?> _entered = Signal();
        private readonly TaskCompletionSource<object?> _release = Signal();

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult(null);

        public async Task DispatchAsync(
            TriggerExecution execution,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult(null);
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class TriggerTestDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.TriggerEvaluationTests",
            Guid.NewGuid().ToString("N"));

        public TriggerTestDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string DatabasePath => Path.Combine(_path, "Triggers.db");

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
