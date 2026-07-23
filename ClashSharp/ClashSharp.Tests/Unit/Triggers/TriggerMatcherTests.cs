using ClashSharp.Model.Triggers;

namespace ClashSharp.Tests.Unit.Triggers;

/// <summary>Verifies pure trigger matching and persistent re-arm transitions.</summary>
public sealed class TriggerMatcherTests
{
    [Fact]
    public void Evaluate_MultipleEdgeConditions_UsesAndWithoutConsumingEarlyMatch()
    {
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "runtime",
                    TriggerConditionKind.Runtime,
                    new RuntimeConditionParameters(TimeSpan.FromSeconds(10))),
                new TriggerCondition(
                    "connections",
                    TriggerConditionKind.ActiveConnections,
                    new ActiveConnectionsConditionParameters(5)),
            ]);
        TriggerTaskState initial = TriggerTaskState.CreateInitial(definition, version: 4);

        TriggerMatchDecision first = TriggerMatcher.Evaluate(
            definition,
            initial,
            Context(runtime: TimeSpan.FromSeconds(11), activeConnections: 4));
        TriggerMatchDecision second = TriggerMatcher.Evaluate(
            definition,
            first.NextState,
            Context(runtime: TimeSpan.FromSeconds(12), activeConnections: 5));

        Assert.Equal(TriggerMatchOutcome.NotMatched, first.Outcome);
        Assert.True(first.NextState.ConditionStates["runtime"].IsArmed);
        Assert.Equal(TriggerMatchOutcome.Matched, second.Outcome);
        Assert.False(second.NextState.ConditionStates["runtime"].IsArmed);
        Assert.False(second.NextState.ConditionStates["connections"].IsArmed);
        Assert.Equal(4, second.ExpectedStateVersion);
    }

    [Fact]
    public void Evaluate_EventCondition_MatchesOnlyExactEventInstance()
    {
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.ProxyStarted)),
            ]);
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition);

        TriggerMatchDecision wrong = TriggerMatcher.Evaluate(
            definition,
            state,
            Context(eventKind: TriggerEventKind.AppEntered));
        TriggerMatchDecision exact = TriggerMatcher.Evaluate(
            definition,
            wrong.NextState,
            Context(eventKind: TriggerEventKind.ProxyStarted));

        Assert.Equal(TriggerMatchOutcome.NotMatched, wrong.Outcome);
        Assert.Equal(TriggerMatchOutcome.Matched, exact.Outcome);
    }

    [Fact]
    public void Evaluate_SystemTime_ConsumesAtMostOneMatchPerLocalDate()
    {
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "daily",
                    TriggerConditionKind.SystemTime,
                    new SystemTimeConditionParameters(new TimeOnly(9, 30))),
            ]);
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition);

        TriggerMatchDecision before = TriggerMatcher.Evaluate(
            definition,
            state,
            Context(localDate: new DateOnly(2026, 7, 23), localTime: new TimeOnly(9, 29)));
        TriggerMatchDecision first = TriggerMatcher.Evaluate(
            definition,
            before.NextState,
            Context(localDate: new DateOnly(2026, 7, 23), localTime: new TimeOnly(9, 30)));
        TriggerMatchDecision duplicate = TriggerMatcher.Evaluate(
            definition,
            first.NextState,
            Context(localDate: new DateOnly(2026, 7, 23), localTime: new TimeOnly(23, 59)));
        TriggerMatchDecision nextDay = TriggerMatcher.Evaluate(
            definition,
            duplicate.NextState,
            Context(localDate: new DateOnly(2026, 7, 24), localTime: new TimeOnly(9, 31)));

        Assert.Equal(TriggerMatchOutcome.NotMatched, before.Outcome);
        Assert.Equal(TriggerMatchOutcome.Matched, first.Outcome);
        Assert.Equal(new DateOnly(2026, 7, 23), first.NextState.ConditionStates["daily"].ConsumedDate);
        Assert.Equal(TriggerMatchOutcome.NotMatched, duplicate.Outcome);
        Assert.Equal(TriggerMatchOutcome.Matched, nextDay.Outcome);
        Assert.Equal(new DateOnly(2026, 7, 24), nextDay.NextState.ConditionStates["daily"].ConsumedDate);
    }

    [Fact]
    public void Evaluate_RateThreshold_RearmsOnlyAfterObservedFalse()
    {
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "upload",
                    TriggerConditionKind.Rate,
                    new RateConditionParameters(TriggerTrafficDirection.Upload, 100)),
            ]);
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition);

        TriggerMatchDecision below = TriggerMatcher.Evaluate(definition, state, Context(uploadRate: 99));
        TriggerMatchDecision first = TriggerMatcher.Evaluate(definition, below.NextState, Context(uploadRate: 100));
        TriggerMatchDecision remainsTrue = TriggerMatcher.Evaluate(definition, first.NextState, Context(uploadRate: 200));
        TriggerMatchDecision unavailable = TriggerMatcher.Evaluate(definition, remainsTrue.NextState, Context());
        TriggerMatchDecision observedFalse = TriggerMatcher.Evaluate(definition, unavailable.NextState, Context(uploadRate: 20));
        TriggerMatchDecision second = TriggerMatcher.Evaluate(definition, observedFalse.NextState, Context(uploadRate: 100));

        Assert.Equal(TriggerMatchOutcome.NotMatched, below.Outcome);
        Assert.Equal(TriggerMatchOutcome.Matched, first.Outcome);
        Assert.Equal(TriggerMatchOutcome.NotMatched, remainsTrue.Outcome);
        Assert.Equal(TriggerMatchOutcome.InsufficientData, unavailable.Outcome);
        Assert.False(unavailable.NextState.ConditionStates["upload"].IsArmed);
        Assert.True(observedFalse.NextState.ConditionStates["upload"].IsArmed);
        Assert.Equal(TriggerMatchOutcome.Matched, second.Outcome);
    }

    [Fact]
    public void Evaluate_RollingWindow_UsesConfiguredDurationValue()
    {
        TimeSpan window = TimeSpan.FromMinutes(15);
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "window",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(TriggerTrafficScope.RollingWindow, 500, window)),
            ]);
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition);

        TriggerMatchDecision missingWindow = TriggerMatcher.Evaluate(
            definition,
            state,
            Context(rollingTraffic: new Dictionary<TimeSpan, long> { [TimeSpan.FromMinutes(5)] = 1000 }));
        TriggerMatchDecision exactWindow = TriggerMatcher.Evaluate(
            definition,
            missingWindow.NextState,
            Context(rollingTraffic: new Dictionary<TimeSpan, long> { [window] = 500 }));

        Assert.Equal(TriggerMatchOutcome.InsufficientData, missingWindow.Outcome);
        Assert.Equal(["window"], missingWindow.UnavailableConditionIds);
        Assert.Equal(TriggerMatchOutcome.Matched, exactWindow.Outcome);
    }

    [Fact]
    public void Evaluate_AllTimeTraffic_ConsumesCurrentRevisionAndEditingRearms()
    {
        TriggerCondition condition = new(
            "all-time",
            TriggerConditionKind.Traffic,
            new TrafficConditionParameters(TriggerTrafficScope.AllTime, 1000));
        TriggerTaskDefinition revisionOne = Definition(revision: 1, conditions: [condition]);
        DateTimeOffset lastTriggeredAt = new(2026, 7, 22, 10, 11, 12, TimeSpan.Zero);
        TriggerTaskState state = TriggerTaskState.CreateInitial(
            revisionOne,
            version: 9,
            lastTriggeredAt);

        TriggerMatchDecision first = TriggerMatcher.Evaluate(revisionOne, state, Context(allTimeTraffic: 1000));
        TriggerMatchDecision duplicate = TriggerMatcher.Evaluate(revisionOne, first.NextState, Context(allTimeTraffic: 2000));
        TriggerTaskDefinition revisionTwo = Definition(revision: 2, conditions: [condition]);
        TriggerMatchDecision edited = TriggerMatcher.Evaluate(revisionTwo, duplicate.NextState, Context(allTimeTraffic: 2000));

        Assert.Equal(TriggerMatchOutcome.Matched, first.Outcome);
        Assert.Equal(1, first.NextState.ConditionStates["all-time"].ConsumedRevision);
        Assert.Equal(TriggerMatchOutcome.NotMatched, duplicate.Outcome);
        Assert.Equal(TriggerMatchOutcome.Matched, edited.Outcome);
        Assert.Equal(2, edited.NextState.TaskRevision);
        Assert.Equal(2, edited.NextState.ConditionStates["all-time"].ConsumedRevision);
        Assert.Equal(9, edited.ExpectedStateVersion);
        Assert.Equal(lastTriggeredAt, edited.NextState.LastTriggeredAt);
    }

    [Fact]
    public void Evaluate_NotificationCondition_RequiresNotificationEventAndMinimumLevel()
    {
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "notification",
                    TriggerConditionKind.Notification,
                    new NotificationConditionParameters(TriggerNotificationLevel.CriticalOnly)),
            ]);
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition);

        TriggerMatchDecision wrongEvent = TriggerMatcher.Evaluate(
            definition,
            state,
            Context(eventKind: TriggerEventKind.AppEntered, notificationLevel: TriggerNotificationLevel.More));
        TriggerMatchDecision lowLevel = TriggerMatcher.Evaluate(
            definition,
            wrongEvent.NextState,
            Context(eventKind: TriggerEventKind.NotificationRaised, notificationLevel: TriggerNotificationLevel.Default));
        TriggerMatchDecision exact = TriggerMatcher.Evaluate(
            definition,
            lowLevel.NextState,
            Context(eventKind: TriggerEventKind.NotificationRaised, notificationLevel: TriggerNotificationLevel.CriticalOnly));

        Assert.Equal(TriggerMatchOutcome.NotMatched, wrongEvent.Outcome);
        Assert.Equal(TriggerMatchOutcome.NotMatched, lowLevel.Outcome);
        Assert.Equal(TriggerMatchOutcome.Matched, exact.Outcome);
    }

    [Fact]
    public void Evaluate_DisabledDefinition_PreservesStateWithoutReadingData()
    {
        TriggerTaskDefinition definition = Definition(
            isEnabled: false,
            conditions:
            [
                new TriggerCondition(
                    "runtime",
                    TriggerConditionKind.Runtime,
                    new RuntimeConditionParameters(TimeSpan.FromMinutes(1))),
            ]);
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition, version: 3);

        TriggerMatchDecision result = TriggerMatcher.Evaluate(definition, state, Context());

        Assert.Equal(TriggerMatchOutcome.NotMatched, result.Outcome);
        Assert.Same(state, result.NextState);
        Assert.Empty(result.UnavailableConditionIds);
    }

    [Fact]
    public void Evaluate_DefiniteFalseAndUnavailableCondition_ReturnsSoundNotMatchedDecision()
    {
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
                new TriggerCondition(
                    "runtime",
                    TriggerConditionKind.Runtime,
                    new RuntimeConditionParameters(TimeSpan.FromMinutes(1))),
            ]);
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition);

        TriggerMatchDecision result = TriggerMatcher.Evaluate(
            definition,
            state,
            Context(eventKind: TriggerEventKind.Periodic));

        Assert.Equal(TriggerMatchOutcome.NotMatched, result.Outcome);
        Assert.Equal(["runtime"], result.UnavailableConditionIds);
    }

    [Fact]
    public void Evaluate_InvalidDefinition_ThrowsBeforeStateTransition()
    {
        TriggerTaskDefinition definition = Definition(conditions: []);
        TriggerTaskState state = TriggerTaskState.CreateInitial(definition);

        Assert.Throws<ArgumentException>(() => TriggerMatcher.Evaluate(definition, state, Context()));
    }

    [Fact]
    public void Evaluate_StateForAnotherTask_ThrowsBeforeTransition()
    {
        TriggerTaskDefinition definition = Definition();
        TriggerTaskState state = new(
            "another-task",
            definition.Revision,
            0,
            new Dictionary<string, TriggerConditionState>());

        Assert.Throws<ArgumentException>(() => TriggerMatcher.Evaluate(definition, state, Context()));
    }

    [Fact]
    public void TriggerTaskState_InvalidPersistedMembers_RejectsConstruction()
    {
        Assert.Throws<ArgumentException>(() => new TriggerTaskState(
            "task",
            1,
            0,
            new Dictionary<string, TriggerConditionState> { ["condition"] = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerTaskState(
            "task",
            0,
            0,
            new Dictionary<string, TriggerConditionState>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerTaskState(
            "task",
            1,
            -1,
            new Dictionary<string, TriggerConditionState>()));
    }

    [Fact]
    public void TriggerEvaluationContext_InvalidObservations_RejectsConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerEvaluationContext(
            (TriggerEventKind)999,
            new DateOnly(2026, 7, 23),
            TimeOnly.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerEvaluationContext(
            TriggerEventKind.Periodic,
            new DateOnly(2026, 7, 23),
            TimeOnly.MinValue,
            uploadBytesPerSecond: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerEvaluationContext(
            TriggerEventKind.NotificationRaised,
            new DateOnly(2026, 7, 23),
            TimeOnly.MinValue,
            notificationLevel: (TriggerNotificationLevel)999));
    }

    [Fact]
    public void TriggerEvaluationContext_CopiesRollingWindowData()
    {
        Dictionary<TimeSpan, long> rolling = new() { [TimeSpan.FromMinutes(5)] = 100 };
        TriggerEvaluationContext context = Context(rollingTraffic: rolling);

        rolling[TimeSpan.FromMinutes(5)] = 200;

        Assert.Equal(100, context.RollingTrafficBytes[TimeSpan.FromMinutes(5)]);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IDictionary<TimeSpan, long>>(context.RollingTrafficBytes)
                .Add(TimeSpan.FromMinutes(10), 300));
    }

    private static TriggerTaskDefinition Definition(
        long revision = 1,
        bool isEnabled = true,
        IReadOnlyList<TriggerCondition>? conditions = null)
    {
        return new TriggerTaskDefinition(
            "task",
            revision,
            "Task",
            isEnabled,
            conditions ??
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("matched")),
            ]);
    }

    private static TriggerEvaluationContext Context(
        TriggerEventKind eventKind = TriggerEventKind.Periodic,
        DateOnly? localDate = null,
        TimeOnly? localTime = null,
        IReadOnlyDictionary<TimeSpan, long>? rollingTraffic = null,
        long? currentSessionTraffic = null,
        long? allTimeTraffic = null,
        long? uploadRate = null,
        long? downloadRate = null,
        int? activeConnections = null,
        TimeSpan? runtime = null,
        TriggerNotificationLevel? notificationLevel = null)
    {
        return new TriggerEvaluationContext(
            eventKind,
            localDate ?? new DateOnly(2026, 7, 23),
            localTime ?? TimeOnly.MinValue,
            rollingTraffic,
            currentSessionTraffic,
            allTimeTraffic,
            uploadRate,
            downloadRate,
            activeConnections,
            runtime,
            notificationLevel);
    }
}
