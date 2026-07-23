using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;
using ClashSharpMode = ClashSharp.Model.ClashSharpMode;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies transactional trigger persistence against a real SQLite database.</summary>
public sealed class SqliteTriggerRepositoryTests
{
    [Fact]
    public async Task OpenAsync_EmptyRootCreatesNormalizedWalDatabase()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();

        TriggerPersistenceResult<TriggerRepositorySnapshot> result =
            await repository.OpenAsync(CancellationToken.None);

        Assert.True(result.IsSucceeded);
        TriggerRepositorySnapshot snapshot = Assert.IsType<TriggerRepositorySnapshot>(result.Value);
        Assert.Equal(TriggerDatabaseSchema.CurrentVersion, snapshot.SchemaVersion);
        Assert.Equal(0, snapshot.DefinitionGeneration);
        Assert.Empty(snapshot.Tasks);
        Assert.Empty(snapshot.Diagnostics);
        await using SqliteConnection connection = await directory.OpenConnectionAsync();
        Assert.Equal("wal", await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(
            [
                "trigger_actions",
                "trigger_condition_states",
                "trigger_conditions",
                "trigger_diagnostics",
                "trigger_executions",
                "trigger_lifecycle_handoffs",
                "trigger_metadata",
                "trigger_outbox",
                "trigger_states",
                "trigger_tasks",
            ],
            await ReadTriggerTableNamesAsync(connection));
    }

    [Fact]
    public async Task OpenAsync_VersionOneDatabaseUpgradesWithoutLosingDefinitions()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        await ReplaceRequiredAsync(repository, 0, [FirstDefinition()]);
        await using (SqliteConnection connection = await directory.OpenConnectionAsync())
        {
            await ExecuteNonQueryAsync(
                connection,
                """
                ALTER TABLE trigger_states DROP COLUMN last_triggered_at;
                UPDATE trigger_metadata SET value = '1' WHERE key = 'schema_version';
                """);
        }

        SqliteTriggerRepository upgradedRepository = directory.CreateRepository();
        TriggerPersistenceResult<TriggerRepositorySnapshot> opened =
            await upgradedRepository.OpenAsync(CancellationToken.None);

        Assert.True(opened.IsSucceeded, opened.Diagnostic?.Code);
        TriggerRepositorySnapshot snapshot = Assert.IsType<TriggerRepositorySnapshot>(opened.Value);
        Assert.Equal(TriggerDatabaseSchema.CurrentVersion, snapshot.SchemaVersion);
        Assert.Equal("first", Assert.Single(snapshot.Tasks).Definition.Id);
        Assert.Null(snapshot.Tasks[0].State.LastTriggeredAt);
        await using SqliteConnection verified = await directory.OpenConnectionAsync();
        Assert.Contains(
            "last_triggered_at",
            await ReadColumnNamesAsync(verified, "trigger_states"));
    }

    [Fact]
    public async Task ReplaceDefinitionsAsync_RoundTripsOrderTypesAndOptimisticGeneration()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        TriggerTaskDefinition first = FirstDefinition();
        TriggerTaskDefinition second = SecondDefinition();

        TriggerPersistenceResult replaced = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(0, [first, second]),
            CancellationToken.None);
        TriggerPersistenceResult conflict = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(0, [second]),
            CancellationToken.None);
        TriggerRepositorySnapshot firstSnapshot = await ReadRequiredAsync(repository);
        TriggerPersistenceResult revisionConflict = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(
                1,
                [
                    new TriggerTaskDefinition(
                        first.Id,
                        first.Revision,
                        "Changed without revision",
                        first.IsEnabled,
                        first.Conditions,
                        first.Actions),
                ]),
            CancellationToken.None);
        TriggerPersistenceResult reordered = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(1, [second, first]),
            CancellationToken.None);
        TriggerRepositorySnapshot secondSnapshot = await ReadRequiredAsync(repository);
        TriggerPersistenceResult removed = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(2, [first]),
            CancellationToken.None);
        TriggerRepositorySnapshot thirdSnapshot = await ReadRequiredAsync(repository);

        Assert.True(replaced.IsSucceeded);
        Assert.Equal(TriggerPersistenceStatus.Conflict, conflict.Status);
        Assert.Equal(TriggerPersistenceStatus.Invalid, revisionConflict.Status);
        Assert.Equal(1, firstSnapshot.DefinitionGeneration);
        AssertDefinitionEqual(first, firstSnapshot.Tasks[0].Definition);
        AssertDefinitionEqual(second, firstSnapshot.Tasks[1].Definition);
        Assert.True(reordered.IsSucceeded);
        Assert.Equal(2, secondSnapshot.DefinitionGeneration);
        AssertDefinitionEqual(second, secondSnapshot.Tasks[0].Definition);
        AssertDefinitionEqual(first, secondSnapshot.Tasks[1].Definition);
        Assert.All(secondSnapshot.Tasks, record => Assert.Equal(0, record.State.Version));
        Assert.True(removed.IsSucceeded);
        Assert.Equal(3, thirdSnapshot.DefinitionGeneration);
        AssertDefinitionEqual(first, Assert.Single(thirdSnapshot.Tasks).Definition);
    }

    [Fact]
    public async Task ReplaceDefinitionsAsync_NewRevisionPreservesTriggerHistoryWhileResettingLatches()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        TriggerTaskDefinition original = FirstDefinition();
        await ReplaceRequiredAsync(repository, 0, [original]);
        TriggerTaskState initialState = (await ReadRequiredAsync(repository)).Tasks[0].State;
        DateTimeOffset triggeredAt = new(2026, 7, 23, 13, 14, 15, TimeSpan.Zero);
        TriggerPersistenceResult<TriggerExecution> committed =
            await repository.TryCommitExecutionAsync(
                new TriggerExecutionCommitRequest(
                    Guid.NewGuid(),
                    original,
                    initialState.Version,
                    initialState,
                    triggeredAt,
                    Guid.NewGuid()),
                CancellationToken.None);
        TriggerTaskDefinition revised = new(
            original.Id,
            original.Revision + 1,
            "Revised",
            original.IsEnabled,
            original.Conditions,
            original.Actions);

        TriggerPersistenceResult replaced = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(1, [revised]),
            CancellationToken.None);
        TriggerTaskState state = (await ReadRequiredAsync(repository)).Tasks[0].State;

        Assert.True(committed.IsSucceeded, committed.Diagnostic?.Code);
        Assert.True(replaced.IsSucceeded, replaced.Diagnostic?.Code);
        Assert.Equal(revised.Revision, state.TaskRevision);
        Assert.Equal(0, state.Version);
        Assert.Equal(triggeredAt, state.LastTriggeredAt);
        Assert.All(state.ConditionStates.Values, condition =>
        {
            Assert.True(condition.IsArmed);
            Assert.Null(condition.ConsumedDate);
            Assert.Null(condition.ConsumedRevision);
        });
    }

    [Fact]
    public async Task ReplaceDefinitionsAsync_RoundTripsEveryTypedConditionAndAction()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        TriggerTaskDefinition definition = new(
            "complete",
            7,
            "Complete",
            true,
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.ProxyStarted)),
                new TriggerCondition(
                    "notification",
                    TriggerConditionKind.Notification,
                    new NotificationConditionParameters(TriggerNotificationLevel.CriticalOnly)),
                new TriggerCondition(
                    "traffic-window",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(
                        TriggerTrafficScope.RollingWindow,
                        1234,
                        TimeSpan.FromSeconds(45))),
                new TriggerCondition(
                    "traffic-session",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(TriggerTrafficScope.CurrentSession, 2345)),
                new TriggerCondition(
                    "traffic-all",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(TriggerTrafficScope.AllTime, 3456)),
                new TriggerCondition(
                    "rate",
                    TriggerConditionKind.Rate,
                    new RateConditionParameters(TriggerTrafficDirection.Download, 4567)),
                new TriggerCondition(
                    "connections",
                    TriggerConditionKind.ActiveConnections,
                    new ActiveConnectionsConditionParameters(8)),
                new TriggerCondition(
                    "runtime",
                    TriggerConditionKind.Runtime,
                    new RuntimeConditionParameters(TimeSpan.FromHours(3))),
                new TriggerCondition(
                    "time",
                    TriggerConditionKind.SystemTime,
                    new SystemTimeConditionParameters(new TimeOnly(1, 2, 3, 4))),
            ],
            [
                new TriggerAction(TriggerActionKind.CloseConnections, new NoActionParameters()),
                new TriggerAction(
                    TriggerActionKind.SetLaunchAtStartup,
                    new BooleanActionParameters(true)),
                new TriggerAction(
                    TriggerActionKind.SetTransparentProxy,
                    new BooleanActionParameters(false)),
                new TriggerAction(
                    TriggerActionKind.SetConnectionSampling,
                    new BooleanActionParameters(true)),
                new TriggerAction(
                    TriggerActionKind.SwitchProxyMode,
                    new ProxyModeActionParameters(ClashSharpMode.RuleTakeover)),
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("all types")),
                new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters()),
            ]);

        await ReplaceRequiredAsync(repository, 0, [definition]);
        TriggerTaskDefinition roundTripped = (await ReadRequiredAsync(repository)).Tasks[0].Definition;

        AssertDefinitionEqual(definition, roundTripped);
    }

    [Fact]
    public async Task TryCommitExecutionAsync_AtomicallyAdvancesLatchAndEnqueuesEveryAction()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        TriggerTaskDefinition definition = FirstDefinition();
        await ReplaceRequiredAsync(repository, 0, [definition]);
        TriggerTaskState initialState = (await ReadRequiredAsync(repository)).Tasks[0].State;
        TriggerTaskState nextState = new(
            definition.Id,
            definition.Revision,
            initialState.Version,
            new Dictionary<string, TriggerConditionState>
            {
                [definition.Conditions[0].Id] = new TriggerConditionState(IsArmed: false),
                [definition.Conditions[1].Id] = new TriggerConditionState(IsArmed: false),
            });
        Guid executionId = Guid.NewGuid();
        TriggerExecutionCommitRequest request = new(
            executionId,
            definition,
            initialState.Version,
            nextState,
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            Guid.NewGuid());

        TriggerPersistenceResult<TriggerExecution> committed =
            await repository.TryCommitExecutionAsync(request, CancellationToken.None);
        TriggerPersistenceResult<TriggerExecution> conflict =
            await repository.TryCommitExecutionAsync(request, CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await ReadRequiredAsync(repository);
        IReadOnlyList<TriggerOutboxAction> recoverable = await ReadRecoverableRequiredAsync(repository);

        Assert.True(committed.IsSucceeded);
        Assert.Equal(executionId, Assert.IsType<TriggerExecution>(committed.Value).ExecutionId);
        Assert.Equal(TriggerPersistenceStatus.Conflict, conflict.Status);
        Assert.Equal(initialState.Version + 1, snapshot.Tasks[0].State.Version);
        Assert.All(snapshot.Tasks[0].State.ConditionStates.Values, state => Assert.False(state.IsArmed));
        Assert.Equal(definition.Actions.Count, recoverable.Count);
        Assert.Equal([0, 1], recoverable.Select(action => action.ActionIndex));
        Assert.All(recoverable, action => Assert.Equal(TriggerOutboxState.Pending, action.State));
    }

    [Fact]
    public async Task TryCommitExecutionAsync_FailureBeforeCommitLeavesNoPartialState()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository setupRepository = directory.CreateRepository();
        await OpenRequiredAsync(setupRepository);
        TriggerTaskDefinition definition = FirstDefinition();
        await ReplaceRequiredAsync(setupRepository, 0, [definition]);
        TriggerTaskState state = (await ReadRequiredAsync(setupRepository)).Tasks[0].State;
        SqliteTriggerRepository faultedRepository = directory.CreateRepository(
            new ThrowingFaultInjector(TriggerPersistenceFaultPoint.BeforeExecutionCommit));
        await OpenRequiredAsync(faultedRepository);

        TriggerPersistenceResult<TriggerExecution> result =
            await faultedRepository.TryCommitExecutionAsync(
                new TriggerExecutionCommitRequest(
                    Guid.NewGuid(),
                    definition,
                    state.Version,
                    state,
                    DateTimeOffset.UnixEpoch,
                    Guid.NewGuid()),
                CancellationToken.None);

        Assert.Equal(TriggerPersistenceStatus.Unavailable, result.Status);
        Assert.Equal(state.Version, (await ReadRequiredAsync(setupRepository)).Tasks[0].State.Version);
        Assert.Empty(await ReadRecoverableRequiredAsync(setupRepository));
    }

    [Fact]
    public async Task TryCommitExecutionAsync_FailureAfterCommitIsRecoverablyInconclusive()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository setupRepository = directory.CreateRepository();
        await OpenRequiredAsync(setupRepository);
        TriggerTaskDefinition definition = FirstDefinition();
        await ReplaceRequiredAsync(setupRepository, 0, [definition]);
        TriggerTaskState state = (await ReadRequiredAsync(setupRepository)).Tasks[0].State;
        SqliteTriggerRepository faultedRepository = directory.CreateRepository(
            new ThrowingFaultInjector(TriggerPersistenceFaultPoint.AfterExecutionCommit));
        await OpenRequiredAsync(faultedRepository);

        TriggerPersistenceResult<TriggerExecution> result =
            await faultedRepository.TryCommitExecutionAsync(
                new TriggerExecutionCommitRequest(
                    Guid.NewGuid(),
                    definition,
                    state.Version,
                    state,
                    DateTimeOffset.UnixEpoch,
                    Guid.NewGuid()),
                CancellationToken.None);

        Assert.Equal(TriggerPersistenceStatus.Unavailable, result.Status);
        Assert.Equal(state.Version + 1, (await ReadRequiredAsync(setupRepository)).Tasks[0].State.Version);
        Assert.Equal(definition.Actions.Count, (await ReadRecoverableRequiredAsync(setupRepository)).Count);
    }

    [Fact]
    public async Task TransitionOutboxAsync_UsesExpectedStateAndUpdatesRecoverableSet()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        TriggerExecution execution = await SeedExecutionAsync(repository);

        TriggerPersistenceResult<TriggerOutboxAction> running = await repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                0,
                TriggerOutboxState.Pending,
                TriggerOutboxState.Running),
            CancellationToken.None);
        TriggerPersistenceResult<TriggerOutboxAction> stale = await repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                0,
                TriggerOutboxState.Pending,
                TriggerOutboxState.Running),
            CancellationToken.None);
        TriggerPersistenceResult<TriggerOutboxAction> reset = await repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                0,
                TriggerOutboxState.Running,
                TriggerOutboxState.Pending),
            CancellationToken.None);
        TriggerPersistenceResult<TriggerOutboxAction> retried = await repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                0,
                TriggerOutboxState.Pending,
                TriggerOutboxState.Running),
            CancellationToken.None);
        TriggerPersistenceResult<TriggerOutboxAction> succeeded = await repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                0,
                TriggerOutboxState.Running,
                TriggerOutboxState.Succeeded),
            CancellationToken.None);

        Assert.Equal(1, Assert.IsType<TriggerOutboxAction>(running.Value).AttemptCount);
        Assert.Equal(TriggerPersistenceStatus.Conflict, stale.Status);
        Assert.Equal(TriggerOutboxState.Pending, reset.Value?.State);
        Assert.Equal(2, retried.Value?.AttemptCount);
        Assert.Equal(TriggerOutboxState.Succeeded, Assert.IsType<TriggerOutboxAction>(succeeded.Value).State);
        Assert.DoesNotContain(
            await ReadRecoverableRequiredAsync(repository),
            action => action.ExecutionId == execution.ExecutionId && action.ActionIndex == 0);
    }

    [Fact]
    public async Task TransitionLifecycleHandoffAsync_InsertsAndComparesExpectedState()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        TriggerExecution execution = await SeedExecutionAsync(repository);
        Guid processEpoch = execution.ProcessEpoch;
        TriggerPersistenceResult<TriggerLifecycleHandoff> premature =
            await repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    execution.ExecutionId,
                    1,
                    processEpoch,
                    null,
                    TriggerLifecycleHandoffState.HandedOff,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);
        TriggerPersistenceResult<TriggerOutboxAction> running = await repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                1,
                TriggerOutboxState.Pending,
                TriggerOutboxState.Running),
            CancellationToken.None);

        TriggerPersistenceResult<TriggerLifecycleHandoff> inserted =
            await repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    execution.ExecutionId,
                    1,
                    processEpoch,
                    null,
                    TriggerLifecycleHandoffState.HandedOff,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);
        TriggerPersistenceResult<TriggerLifecycleHandoff> stale =
            await repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    execution.ExecutionId,
                    1,
                    processEpoch,
                    TriggerLifecycleHandoffState.ReleaseAcknowledged,
                    TriggerLifecycleHandoffState.ShutdownStarted,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);
        TriggerPersistenceResult<TriggerLifecycleHandoff> acknowledged =
            await repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    execution.ExecutionId,
                    1,
                    processEpoch,
                    TriggerLifecycleHandoffState.HandedOff,
                    TriggerLifecycleHandoffState.ReleaseAcknowledged,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);

        Assert.Equal(TriggerPersistenceStatus.Invalid, premature.Status);
        Assert.True(running.IsSucceeded);
        Assert.Equal(TriggerLifecycleHandoffState.HandedOff, inserted.Value?.State);
        Assert.Equal(TriggerPersistenceStatus.Conflict, stale.Status);
        Assert.Equal(TriggerLifecycleHandoffState.ReleaseAcknowledged, acknowledged.Value?.State);
    }

    [Fact]
    public async Task Restart_RetainsDefinitionsLatchesExecutionsAndOutbox()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository firstRepository = directory.CreateRepository();
        TriggerExecution execution = await SeedExecutionAsync(firstRepository);

        SqliteTriggerRepository secondRepository = directory.CreateRepository();
        TriggerRepositorySnapshot reopened = await OpenRequiredAsync(secondRepository);
        IReadOnlyList<TriggerOutboxAction> actions = await ReadRecoverableRequiredAsync(secondRepository);

        Assert.Single(reopened.Tasks);
        Assert.Equal(1, reopened.Tasks[0].State.Version);
        Assert.Equal(
            execution.ExecutionId,
            Assert.Single(actions, action => action.ActionIndex == 0).ExecutionId);
    }

    [Fact]
    public async Task Restart_RetainsDailyAndAllTimeLatchPayloadsExactly()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository firstRepository = directory.CreateRepository();
        await OpenRequiredAsync(firstRepository);
        TriggerTaskDefinition definition = new(
            "latches",
            6,
            "Latches",
            true,
            [
                new TriggerCondition(
                    "daily",
                    TriggerConditionKind.SystemTime,
                    new SystemTimeConditionParameters(new TimeOnly(8, 30))),
                new TriggerCondition(
                    "all-time",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(TriggerTrafficScope.AllTime, 9999)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("latched")),
            ]);
        await ReplaceRequiredAsync(firstRepository, 0, [definition]);
        TriggerTaskState state = (await ReadRequiredAsync(firstRepository)).Tasks[0].State;
        TriggerTaskState nextState = new(
            definition.Id,
            definition.Revision,
            state.Version,
            new Dictionary<string, TriggerConditionState>
            {
                ["daily"] = new TriggerConditionState(
                    IsArmed: true,
                    ConsumedDate: new DateOnly(2026, 7, 23)),
                ["all-time"] = new TriggerConditionState(
                    IsArmed: false,
                    ConsumedRevision: definition.Revision),
            });
        TriggerPersistenceResult<TriggerExecution> committed =
            await firstRepository.TryCommitExecutionAsync(
                new TriggerExecutionCommitRequest(
                    Guid.NewGuid(),
                    definition,
                    state.Version,
                    nextState,
                    DateTimeOffset.UnixEpoch,
                    Guid.NewGuid()),
                CancellationToken.None);

        TriggerRepositorySnapshot reopened = await OpenRequiredAsync(directory.CreateRepository());

        Assert.True(committed.IsSucceeded);
        Assert.Equal(new DateOnly(2026, 7, 23), reopened.Tasks[0].State.ConditionStates["daily"].ConsumedDate);
        Assert.Equal(definition.Revision, reopened.Tasks[0].State.ConditionStates["all-time"].ConsumedRevision);
        Assert.False(reopened.Tasks[0].State.ConditionStates["all-time"].IsArmed);
    }

    [Fact]
    public async Task WalWriter_DoesNotBlockIndependentSnapshotReaders()
    {
        using TemporaryTriggerDirectory directory = new();
        SqliteTriggerRepository repository = directory.CreateRepository();
        await OpenRequiredAsync(repository);
        await ReplaceRequiredAsync(repository, 0, [FirstDefinition()]);
        await using SqliteConnection writer = await directory.OpenConnectionAsync();
        await ExecuteNonQueryAsync(writer, "BEGIN IMMEDIATE;");
        await ExecuteNonQueryAsync(
            writer,
            "UPDATE trigger_metadata SET value = '99' WHERE key = 'definition_generation';");

        try
        {
            TriggerPersistenceResult<TriggerRepositorySnapshot>[] reads = await Task.WhenAll(
                Enumerable.Range(0, 16)
                    .Select(_ => repository.ReadSnapshotAsync(CancellationToken.None)));

            Assert.All(reads, read =>
            {
                Assert.True(read.IsSucceeded);
                Assert.Equal(1, read.Value?.DefinitionGeneration);
            });
        }
        finally
        {
            await ExecuteNonQueryAsync(writer, "ROLLBACK;");
        }
    }

    private static async Task<TriggerExecution> SeedExecutionAsync(SqliteTriggerRepository repository)
    {
        TriggerRepositorySnapshot opened = await OpenRequiredAsync(repository);
        TriggerTaskDefinition definition = FirstDefinition();
        await ReplaceRequiredAsync(repository, opened.DefinitionGeneration, [definition]);
        TriggerTaskState state = (await ReadRequiredAsync(repository)).Tasks[0].State;
        TriggerExecutionCommitRequest request = new(
            Guid.NewGuid(),
            definition,
            state.Version,
            state,
            DateTimeOffset.UnixEpoch,
            Guid.NewGuid());
        TriggerPersistenceResult<TriggerExecution> result =
            await repository.TryCommitExecutionAsync(request, CancellationToken.None);
        Assert.True(result.IsSucceeded);
        return Assert.IsType<TriggerExecution>(result.Value);
    }

    private static async Task<TriggerRepositorySnapshot> OpenRequiredAsync(
        SqliteTriggerRepository repository)
    {
        TriggerPersistenceResult<TriggerRepositorySnapshot> result =
            await repository.OpenAsync(CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsType<TriggerRepositorySnapshot>(result.Value);
    }

    private static async Task<TriggerRepositorySnapshot> ReadRequiredAsync(
        SqliteTriggerRepository repository)
    {
        TriggerPersistenceResult<TriggerRepositorySnapshot> result =
            await repository.ReadSnapshotAsync(CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsType<TriggerRepositorySnapshot>(result.Value);
    }

    private static async Task<IReadOnlyList<TriggerOutboxAction>> ReadRecoverableRequiredAsync(
        SqliteTriggerRepository repository)
    {
        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> result =
            await repository.ReadRecoverableActionsAsync(CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsAssignableFrom<IReadOnlyList<TriggerOutboxAction>>(result.Value);
    }

    private static async Task ReplaceRequiredAsync(
        SqliteTriggerRepository repository,
        long expectedGeneration,
        IEnumerable<TriggerTaskDefinition> definitions)
    {
        TriggerPersistenceResult result = await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(expectedGeneration, definitions),
            CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
    }

    private static TriggerTaskDefinition FirstDefinition()
    {
        return new TriggerTaskDefinition(
            "first",
            4,
            "First",
            true,
            [
                new TriggerCondition(
                    "entered",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
                new TriggerCondition(
                    "traffic",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(
                        TriggerTrafficScope.RollingWindow,
                        4096,
                        TimeSpan.FromMinutes(7))),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SetConnectionSampling,
                    new BooleanActionParameters(true)),
                new TriggerAction(
                    TriggerActionKind.ExitApplication,
                    new NoActionParameters()),
            ]);
    }

    private static TriggerTaskDefinition SecondDefinition()
    {
        return new TriggerTaskDefinition(
            "second",
            2,
            "Second",
            false,
            [
                new TriggerCondition(
                    "time",
                    TriggerConditionKind.SystemTime,
                    new SystemTimeConditionParameters(new TimeOnly(23, 45, 12))),
            ],
            [new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters())]);
    }

    private static void AssertDefinitionEqual(
        TriggerTaskDefinition expected,
        TriggerTaskDefinition actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.Conditions, actual.Conditions);
        Assert.Equal(expected.Actions, actual.Actions);
    }

    private static async Task<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return Assert.IsType<string>(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<string[]> ReadTriggerTableNamesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'trigger_%' ORDER BY name;";
        List<string> names = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task<string[]> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($tableName) ORDER BY cid;";
        command.Parameters.AddWithValue("$tableName", tableName);
        List<string> names = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed class TemporaryTriggerDirectory : IDisposable
    {
        public TemporaryTriggerDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "ClashSharp",
                "TriggerRepositoryTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string DatabasePath => Path.Combine(RootPath, "Triggers.db");

        public SqliteTriggerRepository CreateRepository(
            ITriggerPersistenceFaultInjector? faultInjector = null)
        {
            return new SqliteTriggerRepository(
                DatabasePath,
                faultInjector,
                busyTimeout: TimeSpan.FromSeconds(1));
        }

        public async Task<SqliteConnection> OpenConnectionAsync()
        {
            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
            };
            SqliteConnection connection = new(builder.ToString());
            await connection.OpenAsync(CancellationToken.None);
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class ThrowingFaultInjector(TriggerPersistenceFaultPoint faultPoint)
        : ITriggerPersistenceFaultInjector
    {
        public Task InjectAsync(
            TriggerPersistenceFaultPoint observedPoint,
            CancellationToken cancellationToken)
        {
            if (observedPoint == faultPoint)
            {
                throw new IOException("Injected trigger persistence failure.");
            }

            return Task.CompletedTask;
        }
    }
}
