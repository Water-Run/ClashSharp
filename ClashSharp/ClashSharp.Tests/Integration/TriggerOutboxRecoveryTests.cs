using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model.Triggers;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies durable outbox recovery across transaction and external-effect cut points.</summary>
public sealed class TriggerOutboxRecoveryTests
{
    [Fact]
    public async Task ReconcileAsync_RunningAppliedEffectIsNotRepeatedAndLaterActionContinues()
    {
        using TriggerOutboxDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution) =
            await SeedExecutionAsync(directory);
        IReadOnlyList<TriggerOutboxAction> initial = await ReadActionsAsync(repository, execution.ExecutionId);
        TriggerPersistenceResult<TriggerOutboxAction> running = await repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                0,
                TriggerOutboxState.Pending,
                TriggerOutboxState.Running),
            CancellationToken.None);
        Assert.True(running.IsSucceeded, running.Diagnostic?.Code);
        DurableEffectLedger ledger = new();
        ledger.Seed(initial[0].IdempotencyKey);

        IReadOnlyList<TriggerActionResult> results = await ReconcileAsync(repository, ledger);
        IReadOnlyList<TriggerOutboxAction> stored = await ReadActionsAsync(
            repository,
            execution.ExecutionId);

        Assert.Equal(2, results.Count);
        Assert.All(stored, action => Assert.Equal(TriggerOutboxState.Succeeded, action.State));
        Assert.Equal(0, ledger.GetApplyCount(initial[0].IdempotencyKey));
        Assert.Equal(1, ledger.GetApplyCount(initial[1].IdempotencyKey));
    }

    [Fact]
    public async Task ReconcileAsync_CrashAfterExternalEffectDeduplicatesAndContinuesLaterAction()
    {
        using TriggerOutboxDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution) =
            await SeedExecutionAsync(directory);
        IReadOnlyList<TriggerOutboxAction> initial = await ReadActionsAsync(repository, execution.ExecutionId);
        DurableEffectLedger ledger = new(initial[0].IdempotencyKey);
        TriggerActionExecutor executor = new(
            repository,
            new LedgerActionRuntime(ledger),
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using (MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None))
        {
            await Assert.ThrowsAsync<SimulatedProcessCrashException>(() => executor.ExecuteAsync(
                execution,
                lease,
                CancellationToken.None));
        }

        IReadOnlyList<TriggerOutboxAction> interrupted = await ReadActionsAsync(
            repository,
            execution.ExecutionId);
        Assert.Equal(TriggerOutboxState.Running, interrupted[0].State);
        Assert.Equal(TriggerOutboxState.Pending, interrupted[1].State);

        await ReconcileAsync(repository, ledger);
        IReadOnlyList<TriggerOutboxAction> recovered = await ReadActionsAsync(
            repository,
            execution.ExecutionId);

        Assert.All(recovered, action => Assert.Equal(TriggerOutboxState.Succeeded, action.State));
        Assert.Equal(1, ledger.GetApplyCount(initial[0].IdempotencyKey));
        Assert.Equal(1, ledger.GetApplyCount(initial[1].IdempotencyKey));
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeOutboxTransitionCommit, TriggerOutboxState.Pending)]
    [InlineData(TriggerPersistenceFaultPoint.AfterOutboxTransitionCommit, TriggerOutboxState.Running)]
    public async Task ReconcileAsync_TransitionCommitCutRecoversWithoutDuplicateEffect(
        TriggerPersistenceFaultPoint faultPoint,
        TriggerOutboxState interruptedState)
    {
        using TriggerOutboxDirectory directory = new();
        (SqliteTriggerRepository setupRepository, TriggerExecution execution) =
            await SeedExecutionAsync(directory);
        IReadOnlyList<TriggerOutboxAction> initial = await ReadActionsAsync(
            setupRepository,
            execution.ExecutionId);
        SqliteTriggerRepository faultedRepository = new(
            directory.DatabasePath,
            new OneShotFaultInjector(faultPoint));
        Assert.True((await faultedRepository.OpenAsync(CancellationToken.None)).IsSucceeded);
        DurableEffectLedger ledger = new();
        TriggerActionExecutor executor = new(
            faultedRepository,
            new LedgerActionRuntime(ledger),
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using (MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
                execution,
                lease,
                CancellationToken.None));
        }

        IReadOnlyList<TriggerOutboxAction> interrupted = await ReadActionsAsync(
            setupRepository,
            execution.ExecutionId);
        Assert.Equal(interruptedState, interrupted[0].State);
        Assert.Equal(0, ledger.GetApplyCount(initial[0].IdempotencyKey));

        await ReconcileAsync(setupRepository, ledger);
        IReadOnlyList<TriggerOutboxAction> recovered = await ReadActionsAsync(
            setupRepository,
            execution.ExecutionId);

        Assert.All(recovered, action => Assert.Equal(TriggerOutboxState.Succeeded, action.State));
        Assert.Equal(1, ledger.GetApplyCount(initial[0].IdempotencyKey));
        Assert.Equal(1, ledger.GetApplyCount(initial[1].IdempotencyKey));
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeOutboxTransitionCommit, TriggerOutboxState.Running)]
    [InlineData(TriggerPersistenceFaultPoint.AfterOutboxTransitionCommit, TriggerOutboxState.Succeeded)]
    public async Task ReconcileAsync_TerminalCommitCutRecoversWithoutDuplicateEffectOrLostLaterAction(
        TriggerPersistenceFaultPoint faultPoint,
        TriggerOutboxState interruptedState)
    {
        using TriggerOutboxDirectory directory = new();
        (SqliteTriggerRepository setupRepository, TriggerExecution execution) =
            await SeedExecutionAsync(directory);
        IReadOnlyList<TriggerOutboxAction> initial = await ReadActionsAsync(
            setupRepository,
            execution.ExecutionId);
        SqliteTriggerRepository faultedRepository = new(
            directory.DatabasePath,
            new OneShotFaultInjector(faultPoint, occurrence: 2));
        Assert.True((await faultedRepository.OpenAsync(CancellationToken.None)).IsSucceeded);
        DurableEffectLedger ledger = new();
        TriggerActionExecutor executor = new(
            faultedRepository,
            new LedgerActionRuntime(ledger),
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using (MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
                execution,
                lease,
                CancellationToken.None));
        }

        IReadOnlyList<TriggerOutboxAction> interrupted = await ReadActionsAsync(
            setupRepository,
            execution.ExecutionId);
        Assert.Equal(interruptedState, interrupted[0].State);
        Assert.Equal(TriggerOutboxState.Pending, interrupted[1].State);
        Assert.Equal(1, ledger.GetApplyCount(initial[0].IdempotencyKey));

        await ReconcileAsync(setupRepository, ledger);
        IReadOnlyList<TriggerOutboxAction> recovered = await ReadActionsAsync(
            setupRepository,
            execution.ExecutionId);

        Assert.All(recovered, action => Assert.Equal(TriggerOutboxState.Succeeded, action.State));
        Assert.Equal(1, ledger.GetApplyCount(initial[0].IdempotencyKey));
        Assert.Equal(1, ledger.GetApplyCount(initial[1].IdempotencyKey));
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeOutboxTransitionCommit, TriggerOutboxState.Running)]
    [InlineData(TriggerPersistenceFaultPoint.AfterOutboxTransitionCommit, TriggerOutboxState.Pending)]
    public async Task ReconcileAsync_RetryResetCommitCutRecoversWithoutLostAction(
        TriggerPersistenceFaultPoint faultPoint,
        TriggerOutboxState interruptedState)
    {
        using TriggerOutboxDirectory directory = new();
        (SqliteTriggerRepository setupRepository, TriggerExecution execution) =
            await SeedExecutionAsync(directory);
        TriggerPersistenceResult<TriggerOutboxAction> running = await setupRepository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                0,
                TriggerOutboxState.Pending,
                TriggerOutboxState.Running),
            CancellationToken.None);
        Assert.True(running.IsSucceeded, running.Diagnostic?.Code);
        SqliteTriggerRepository faultedRepository = new(
            directory.DatabasePath,
            new OneShotFaultInjector(faultPoint));
        Assert.True((await faultedRepository.OpenAsync(CancellationToken.None)).IsSucceeded);
        DurableEffectLedger ledger = new();
        TriggerActionExecutor executor = new(
            faultedRepository,
            new LedgerActionRuntime(ledger),
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using (MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
                execution,
                lease,
                CancellationToken.None));
        }

        IReadOnlyList<TriggerOutboxAction> interrupted = await ReadActionsAsync(
            setupRepository,
            execution.ExecutionId);
        Assert.Equal(interruptedState, interrupted[0].State);
        Assert.Equal(TriggerOutboxState.Pending, interrupted[1].State);

        await ReconcileAsync(setupRepository, ledger);
        IReadOnlyList<TriggerOutboxAction> recovered = await ReadActionsAsync(
            setupRepository,
            execution.ExecutionId);

        Assert.All(recovered, action => Assert.Equal(TriggerOutboxState.Succeeded, action.State));
        Assert.All(recovered, action => Assert.Equal(1, ledger.GetApplyCount(action.IdempotencyKey)));
    }

    private static async Task<IReadOnlyList<TriggerActionResult>> ReconcileAsync(
        ITriggerRepository repository,
        DurableEffectLedger ledger)
    {
        MutationAdmissionBarrier admission = new();
        TriggerActionExecutor executor = new(
            repository,
            new LedgerActionRuntime(ledger),
            NullTriggerFiredNotificationSink.Instance);
        TriggerActionReconciler reconciler = new(repository, executor, admission);
        return await reconciler.ReconcileAsync(CancellationToken.None);
    }

    private static async Task<(SqliteTriggerRepository Repository, TriggerExecution Execution)>
        SeedExecutionAsync(TriggerOutboxDirectory directory)
    {
        SqliteTriggerRepository repository = new(directory.DatabasePath);
        TriggerPersistenceResult<TriggerRepositorySnapshot> opened = await repository.OpenAsync(
            CancellationToken.None);
        Assert.True(opened.IsSucceeded, opened.Diagnostic?.Code);
        TriggerTaskDefinition definition = Definition();
        TriggerPersistenceResult replaced = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(0, [definition]),
            CancellationToken.None);
        Assert.True(replaced.IsSucceeded, replaced.Diagnostic?.Code);
        TriggerPersistenceResult<TriggerRepositorySnapshot> snapshot = await repository.ReadSnapshotAsync(
            CancellationToken.None);
        TriggerTaskState state = Assert.IsType<TriggerRepositorySnapshot>(snapshot.Value).Tasks[0].State;
        TriggerPersistenceResult<TriggerExecution> committed = await repository.TryCommitExecutionAsync(
            new TriggerExecutionCommitRequest(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                definition,
                state.Version,
                state,
                DateTimeOffset.UnixEpoch,
                Guid.Parse("22222222-2222-2222-2222-222222222222")),
            CancellationToken.None);
        Assert.True(committed.IsSucceeded, committed.Diagnostic?.Code);
        return (repository, Assert.IsType<TriggerExecution>(committed.Value));
    }

    private static TriggerTaskDefinition Definition()
    {
        return new TriggerTaskDefinition(
            "task",
            1,
            "Task",
            isEnabled: true,
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("first")),
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("second")),
            ]);
    }

    private static async Task<IReadOnlyList<TriggerOutboxAction>> ReadActionsAsync(
        ITriggerRepository repository,
        Guid executionId)
    {
        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> result =
            await repository.ReadExecutionActionsAsync(executionId, CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsAssignableFrom<IReadOnlyList<TriggerOutboxAction>>(result.Value);
    }

    private sealed class LedgerActionRuntime(DurableEffectLedger ledger) : ITriggerActionRuntime
    {
        public Task<TriggerActionProbeResult> ProbeAsync(
            TriggerOutboxAction action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ledger.Contains(action.IdempotencyKey)
                ? TriggerActionProbeResult.Desired()
                : TriggerActionProbeResult.NotDesired());
        }

        public Task<TriggerActionApplyResult> ApplyAsync(
            TriggerOutboxAction action,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ledger.Apply(action.IdempotencyKey);
            return Task.FromResult(TriggerActionApplyResult.Applied());
        }
    }

    private sealed class DurableEffectLedger(string? crashAfterKey = null)
    {
        private readonly Dictionary<string, int> _applyCounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _effects = new(StringComparer.Ordinal);
        private bool _crashArmed = crashAfterKey is not null;

        public bool Contains(string key) => _effects.Contains(key);

        public int GetApplyCount(string key) => _applyCounts.GetValueOrDefault(key);

        public void Seed(string key)
        {
            _effects.Add(key);
        }

        public void Apply(string key)
        {
            if (_effects.Add(key))
            {
                _applyCounts[key] = GetApplyCount(key) + 1;
            }

            if (_crashArmed && StringComparer.Ordinal.Equals(key, crashAfterKey))
            {
                _crashArmed = false;
                throw new SimulatedProcessCrashException();
            }
        }
    }

    private sealed class OneShotFaultInjector(
        TriggerPersistenceFaultPoint target,
        int occurrence = 1)
        : ITriggerPersistenceFaultInjector
    {
        private int _remaining = occurrence;

        public Task InjectAsync(
            TriggerPersistenceFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            if (faultPoint == target && Interlocked.Decrement(ref _remaining) == 0)
            {
                throw new IOException("Injected outbox transition failure.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SimulatedProcessCrashException : Exception;

    private sealed class TriggerOutboxDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.TriggerOutboxTests",
            Guid.NewGuid().ToString("N"));

        public TriggerOutboxDirectory()
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
