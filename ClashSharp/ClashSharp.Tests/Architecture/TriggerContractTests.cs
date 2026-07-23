using System.Globalization;
using System.Reflection;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;

namespace ClashSharp.Tests.Architecture;

/// <summary>Locks the durable trigger repository and execution contracts before infrastructure exists.</summary>
public sealed class TriggerContractTests
{
    [Fact]
    public void RepositorySnapshot_CopiesOrderedTasksAndDiagnostics()
    {
        TriggerTaskDefinition definition = Definition();
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition, version: 3);
        List<TriggerTaskRecord> tasks = [new TriggerTaskRecord(0, definition, state)];
        List<TriggerDiagnostic> diagnostics =
        [
            new TriggerDiagnostic(
                "trigger.storage.recovered",
                TriggerDiagnosticSeverity.Warning,
                definition.Id,
                "backup",
                DateTimeOffset.UnixEpoch),
        ];

        TriggerRepositorySnapshot snapshot = new(1, 5, tasks, diagnostics);
        tasks.Clear();
        diagnostics.Clear();

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal(5, snapshot.DefinitionGeneration);
        Assert.Single(snapshot.Tasks);
        Assert.Single(snapshot.Diagnostics);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<TriggerTaskRecord>>(snapshot.Tasks)
                .Add(new TriggerTaskRecord(1, definition, state)));
    }

    [Fact]
    public void RepositorySnapshot_InvalidOrderOrStateIdentity_RejectsConstruction()
    {
        TriggerTaskDefinition definition = Definition();
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition);

        Assert.Throws<ArgumentException>(() => new TriggerRepositorySnapshot(
            1,
            0,
            [new TriggerTaskRecord(0, definition, state), new TriggerTaskRecord(0, definition, state)],
            []));
        Assert.Throws<ArgumentException>(() => new TriggerRepositorySnapshot(
            1,
            0,
            [
                new TriggerTaskRecord(
                    0,
                    definition,
                    new TriggerTaskState("another", 1, 0, new Dictionary<string, TriggerConditionState>())),
            ],
            []));
        Assert.Throws<ArgumentException>(() => new TriggerRepositorySnapshot(
            1,
            0,
            [
                new TriggerTaskRecord(
                    0,
                    definition,
                    new TriggerTaskState(
                        definition.Id,
                        definition.Revision,
                        0,
                        new Dictionary<string, TriggerConditionState>
                        {
                            ["unexpected"] = new TriggerConditionState(),
                        })),
            ],
            []));
    }

    [Fact]
    public void ExecutionCommitRequest_BuildsCompleteDeterministicPendingOutbox()
    {
        TriggerTaskDefinition definition = Definition(
            actions:
            [
                new TriggerAction(TriggerActionKind.SetConnectionSampling, new BooleanActionParameters(true)),
                new TriggerAction(TriggerActionKind.SendNotification, new NotificationActionParameters("done")),
            ]);
        TriggerTaskState nextState = TriggerTaskState.CreateInitial(definition, version: 8);
        Guid executionId = Guid.Parse("db8f8a47-844d-4396-b675-9c91db0f5fa0");
        Guid processEpoch = Guid.Parse("443bb4c7-c68d-42cd-8413-88a65b15ec73");

        TriggerExecutionCommitRequest request = new(
            executionId,
            definition,
            expectedStateVersion: 8,
            nextState,
            new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
            processEpoch);

        Assert.Equal(2, request.OutboxActions.Count);
        Assert.All(request.OutboxActions, action => Assert.Equal(TriggerOutboxState.Pending, action.State));
        Assert.Equal([0, 1], request.OutboxActions.Select(action => action.ActionIndex));
        Assert.Equal(definition.Revision, request.OutboxActions[0].TaskRevision);
        Assert.Equal(
            TriggerIdempotencyKey.Create(executionId, definition.Revision, 0),
            request.OutboxActions[0].IdempotencyKey);
        Assert.NotEqual(request.OutboxActions[0].IdempotencyKey, request.OutboxActions[1].IdempotencyKey);
        Assert.Equal(processEpoch, request.ProcessEpoch);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<TriggerOutboxAction>>(request.OutboxActions)
                .Add(request.OutboxActions[0]));
    }

    [Fact]
    public void DefinitionWriteRequest_CopiesAndValidatesCompleteReplacement()
    {
        TriggerTaskDefinition definition = Definition();
        List<TriggerTaskDefinition> definitions = [definition];

        TriggerDefinitionWriteRequest request = new(3, definitions);
        definitions.Clear();

        Assert.Equal(3, request.ExpectedGeneration);
        Assert.Single(request.Definitions);
        Assert.Throws<ArgumentException>(() => new TriggerDefinitionWriteRequest(
            0,
            [definition, definition]));
        Assert.Throws<ArgumentException>(() => new TriggerDefinitionWriteRequest(
            0,
            [new TriggerTaskDefinition("invalid", 1, "Invalid", true, [], [])]));
    }

    [Fact]
    public void IdempotencyKey_IsCultureInvariantAndValidatesComponents()
    {
        Guid executionId = Guid.Parse("db8f8a47-844d-4396-b675-9c91db0f5fa0");
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            Assert.Equal(
                "db8f8a47844d4396b6759c91db0f5fa0:1234:5",
                TriggerIdempotencyKey.Create(executionId, 1234, 5));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.Throws<ArgumentException>(() => TriggerIdempotencyKey.Create(Guid.Empty, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerIdempotencyKey.Create(executionId, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerIdempotencyKey.Create(executionId, 1, -1));
    }

    [Fact]
    public void DurableValueObjects_RejectInconsistentOrMalformedData()
    {
        TriggerAction action = new(
            TriggerActionKind.SendNotification,
            new NotificationActionParameters("matched"));
        Guid executionId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new TriggerOutboxAction(
            executionId,
            4,
            0,
            "not-the-derived-key",
            action,
            TriggerOutboxState.Pending));
        Assert.Throws<ArgumentException>(() => new TriggerDiagnostic(
            " ",
            TriggerDiagnosticSeverity.Error,
            null,
            "invalid",
            DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerDiagnostic(
            "trigger.invalid",
            (TriggerDiagnosticSeverity)999,
            null,
            "invalid",
            DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => new TriggerDiagnostic(
            "trigger.invalid",
            TriggerDiagnosticSeverity.Error,
            " ",
            "invalid",
            DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ExecutionCommitRequest_MismatchedStateOrEmptyIdentity_RejectsConstruction()
    {
        TriggerTaskDefinition definition = Definition();
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition, version: 2);

        Assert.Throws<ArgumentException>(() => new TriggerExecutionCommitRequest(
            Guid.Empty,
            definition,
            2,
            state,
            DateTimeOffset.UtcNow,
            Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new TriggerExecutionCommitRequest(
            Guid.NewGuid(),
            definition,
            1,
            state,
            DateTimeOffset.UtcNow,
            Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new TriggerExecutionCommitRequest(
            Guid.NewGuid(),
            definition,
            2,
            new TriggerTaskState("another", 1, 2, new Dictionary<string, TriggerConditionState>()),
            DateTimeOffset.UtcNow,
            Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new TriggerExecutionCommitRequest(
            Guid.NewGuid(),
            definition,
            2,
            state,
            DateTimeOffset.UtcNow,
            Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TriggerExecutionCommitRequest(
            Guid.NewGuid(),
            definition,
            2,
            new TriggerTaskState(
                definition.Id,
                definition.Revision,
                2,
                new Dictionary<string, TriggerConditionState>
                {
                    ["unexpected"] = new TriggerConditionState(),
                }),
            DateTimeOffset.UtcNow,
            Guid.NewGuid()));
    }

    [Fact]
    public void TriggerOutboxState_ContainsRequiredDurableStates()
    {
        Assert.Equal(
            ["Pending", "Running", "HandedOff", "Succeeded", "Failed", "Uncertain"],
            Enum.GetNames<TriggerOutboxState>());
    }

    [Fact]
    public void LifecycleHandoff_RecordsExecutionActionAndProcessEpoch()
    {
        Guid executionId = Guid.NewGuid();
        Guid processEpoch = Guid.NewGuid();
        TriggerLifecycleHandoff handoff = new(
            executionId,
            actionIndex: 2,
            processEpoch,
            TriggerLifecycleHandoffState.ReleaseAcknowledged,
            DateTimeOffset.UnixEpoch,
            null);

        Assert.Equal(executionId, handoff.ExecutionId);
        Assert.Equal(2, handoff.ActionIndex);
        Assert.Equal(processEpoch, handoff.ProcessEpoch);
        Assert.Equal(TriggerLifecycleHandoffState.ReleaseAcknowledged, handoff.State);
    }

    [Fact]
    public void TransitionRequests_RejectInvalidPersistenceKeysAndStates()
    {
        Guid executionId = Guid.NewGuid();
        Guid processEpoch = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new TriggerOutboxTransition(
            Guid.Empty,
            0,
            TriggerOutboxState.Pending,
            TriggerOutboxState.Running));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerOutboxTransition(
            executionId,
            -1,
            TriggerOutboxState.Pending,
            TriggerOutboxState.Running));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerOutboxTransition(
            executionId,
            0,
            (TriggerOutboxState)999,
            TriggerOutboxState.Running));
        Assert.Throws<ArgumentException>(() => new TriggerLifecycleHandoffTransition(
            executionId,
            0,
            Guid.Empty,
            null,
            TriggerLifecycleHandoffState.HandedOff,
            DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerLifecycleHandoffTransition(
            executionId,
            0,
            processEpoch,
            (TriggerLifecycleHandoffState)999,
            TriggerLifecycleHandoffState.HandedOff,
            DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void PersistenceResults_DistinguishConflictFromUnavailableAndInvalid()
    {
        TriggerPersistenceResult conflict = TriggerPersistenceResult.Conflict();
        TriggerPersistenceResult unavailable = TriggerPersistenceResult.Unavailable(
            new TriggerDiagnostic(
                "trigger.storage.unavailable",
                TriggerDiagnosticSeverity.Error,
                null,
                "locked",
                DateTimeOffset.UnixEpoch));
        TriggerPersistenceResult invalid = TriggerPersistenceResult.Invalid(
            new TriggerDiagnostic(
                "trigger.definition.invalid",
                TriggerDiagnosticSeverity.Error,
                "task",
                "revision",
                DateTimeOffset.UnixEpoch));

        Assert.Equal(TriggerPersistenceStatus.Conflict, conflict.Status);
        Assert.Equal(TriggerPersistenceStatus.Unavailable, unavailable.Status);
        Assert.Equal(TriggerPersistenceStatus.Invalid, invalid.Status);
        Assert.False(conflict.IsSucceeded);
        Assert.NotNull(unavailable.Diagnostic);
    }

    [Fact]
    public void RepositoryMethods_AreAsynchronousAndCancellationAware()
    {
        MethodInfo[] methods = typeof(ITriggerRepository).GetMethods();

        Assert.NotEmpty(methods);
        Assert.All(methods, method =>
        {
            Assert.True(typeof(Task).IsAssignableFrom(method.ReturnType), method.Name);
            ParameterInfo token = Assert.Single(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(CancellationToken));
            Assert.Equal(method.GetParameters().Length - 1, token.Position);
        });
        Assert.Contains(methods, method => method.Name == nameof(ITriggerRepository.TryCommitExecutionAsync));
        Assert.Contains(methods, method => method.Name == nameof(ITriggerRepository.ReadRecoverableActionsAsync));
        Assert.Contains(methods, method => method.Name == nameof(ITriggerRepository.TransitionLifecycleHandoffAsync));
    }

    private static TriggerTaskDefinition Definition(
        IReadOnlyList<TriggerAction>? actions = null)
    {
        return new TriggerTaskDefinition(
            "task",
            4,
            "Task",
            true,
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            actions ??
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("matched")),
            ]);
    }
}
