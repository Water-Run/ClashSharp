using ClashSharp.Model.Triggers;
using ClashSharpMode = ClashSharp.Model.ClashSharpMode;

namespace ClashSharp.Tests.Unit.Triggers;

/// <summary>Verifies typed trigger definitions reject malformed state before persistence or execution.</summary>
public sealed class TriggerDefinitionValidatorTests
{
    [Fact]
    public void Constructor_CopiesDefinitionCollections()
    {
        List<TriggerCondition> conditions = [EventCondition("entered", TriggerEventKind.AppEntered)];
        List<TriggerAction> actions = [NotificationAction("hello")];

        TriggerTaskDefinition definition = new("task-1", 1, "Task", true, conditions, actions);
        conditions.Add(EventCondition("proxy", TriggerEventKind.ProxyStarted));
        actions.Clear();

        Assert.Single(definition.Conditions);
        Assert.Single(definition.Actions);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<TriggerCondition>>(definition.Conditions).Add(
                EventCondition("other", TriggerEventKind.AppEntered)));
    }

    [Fact]
    public void Validate_AllSupportedTypedParameters_ReturnsValid()
    {
        TriggerTaskDefinition definition = new(
            "all-types",
            7,
            "All typed parameters",
            true,
            [
                EventCondition("entered", TriggerEventKind.AppEntered),
                new TriggerCondition(
                    "notification",
                    TriggerConditionKind.Notification,
                    new NotificationConditionParameters(TriggerNotificationLevel.CriticalOnly)),
                new TriggerCondition(
                    "window",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(
                        TriggerTrafficScope.RollingWindow,
                        1024,
                        TimeSpan.FromMinutes(5))),
                new TriggerCondition(
                    "session",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(TriggerTrafficScope.CurrentSession, 2048)),
                new TriggerCondition(
                    "all-time",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(TriggerTrafficScope.AllTime, 4096)),
                new TriggerCondition(
                    "upload",
                    TriggerConditionKind.Rate,
                    new RateConditionParameters(TriggerTrafficDirection.Upload, 100)),
                new TriggerCondition(
                    "connections",
                    TriggerConditionKind.ActiveConnections,
                    new ActiveConnectionsConditionParameters(2)),
                new TriggerCondition(
                    "runtime",
                    TriggerConditionKind.Runtime,
                    new RuntimeConditionParameters(TimeSpan.FromMinutes(1))),
                new TriggerCondition(
                    "time",
                    TriggerConditionKind.SystemTime,
                    new SystemTimeConditionParameters(new TimeOnly(23, 0))),
            ],
            [
                new TriggerAction(TriggerActionKind.CloseConnections, new NoActionParameters()),
                new TriggerAction(TriggerActionKind.SetLaunchAtStartup, new BooleanActionParameters(true)),
                new TriggerAction(TriggerActionKind.SetTransparentProxy, new BooleanActionParameters(true)),
                new TriggerAction(TriggerActionKind.SetConnectionSampling, new BooleanActionParameters(false)),
                new TriggerAction(TriggerActionKind.SwitchProxyMode, new ProxyModeActionParameters(ClashSharpMode.RuleTakeover)),
                NotificationAction("complete"),
                new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters()),
            ]);

        TriggerDefinitionValidationResult result = TriggerDefinitionValidator.Validate(definition);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_InvalidIdentityRevisionOrCollections_ReturnsStableErrors()
    {
        TriggerTaskDefinition definition = new(
            " ",
            0,
            " ",
            true,
            [
                EventCondition("duplicate", TriggerEventKind.AppEntered),
                EventCondition("duplicate", TriggerEventKind.ProxyStarted),
            ],
            []);

        TriggerDefinitionValidationResult result = TriggerDefinitionValidator.Validate(definition);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "trigger.id.required");
        Assert.Contains(result.Errors, error => error.Code == "trigger.revision.invalid");
        Assert.Contains(result.Errors, error => error.Code == "trigger.name.required");
        Assert.Contains(result.Errors, error => error.Code == "trigger.condition.id.duplicate");
        Assert.Contains(result.Errors, error => error.Code == "trigger.actions.required");
    }

    [Fact]
    public void Validate_NoConditions_ReturnsRequiredError()
    {
        TriggerTaskDefinition definition = new(
            "task",
            1,
            "Task",
            true,
            [],
            [NotificationAction("hello")]);

        TriggerDefinitionValidationResult result = TriggerDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, error => error.Code == "trigger.conditions.required");
    }

    [Fact]
    public void Validate_NullDeserializedMembers_ReturnsErrorsInsteadOfThrowing()
    {
        TriggerTaskDefinition definition = new(
            "task",
            1,
            "Task",
            true,
            [null!, new TriggerCondition("parameters", TriggerConditionKind.Runtime, null!)],
            [null!]);

        TriggerDefinitionValidationResult result = TriggerDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, error => error.Code == "trigger.condition.required");
        Assert.Contains(result.Errors, error => error.Code == "trigger.condition.parameters.mismatch");
        Assert.Contains(result.Errors, error => error.Code == "trigger.action.required");
    }

    [Theory]
    [MemberData(nameof(InvalidConditions))]
    public void Validate_InvalidCondition_ReturnsExpectedError(
        TriggerCondition condition,
        string expectedCode)
    {
        TriggerTaskDefinition definition = ValidDefinition(conditions: [condition]);

        TriggerDefinitionValidationResult result = TriggerDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, error => error.Code == expectedCode);
    }

    [Theory]
    [MemberData(nameof(InvalidActions))]
    public void Validate_InvalidAction_ReturnsExpectedError(
        TriggerAction action,
        string expectedCode)
    {
        TriggerTaskDefinition definition = ValidDefinition(actions: [action]);

        TriggerDefinitionValidationResult result = TriggerDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, error => error.Code == expectedCode);
    }

    [Fact]
    public void Validate_ExitApplicationBeforeLaterAction_ReturnsFinalActionError()
    {
        TriggerTaskDefinition definition = ValidDefinition(
            actions:
            [
                new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters()),
                NotificationAction("unreachable"),
            ]);

        TriggerDefinitionValidationResult result = TriggerDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, error => error.Code == "trigger.action.exit.must_be_final");
    }

    public static TheoryData<TriggerCondition, string> InvalidConditions => new()
    {
        { new TriggerCondition("", TriggerConditionKind.Event, new EventConditionParameters(TriggerEventKind.AppEntered)), "trigger.condition.id.required" },
        { new TriggerCondition("kind", (TriggerConditionKind)999, new EventConditionParameters(TriggerEventKind.AppEntered)), "trigger.condition.kind.undefined" },
        { new TriggerCondition("event", TriggerConditionKind.Event, new EventConditionParameters(TriggerEventKind.Periodic)), "trigger.condition.event.invalid" },
        { new TriggerCondition("event-enum", TriggerConditionKind.Event, new EventConditionParameters((TriggerEventKind)999)), "trigger.condition.event.undefined" },
        { new TriggerCondition("mismatch", TriggerConditionKind.Runtime, new ActiveConnectionsConditionParameters(1)), "trigger.condition.parameters.mismatch" },
        { new TriggerCondition("scope", TriggerConditionKind.Traffic, new TrafficConditionParameters((TriggerTrafficScope)999, 1)), "trigger.condition.traffic.scope.undefined" },
        { new TriggerCondition("threshold", TriggerConditionKind.Traffic, new TrafficConditionParameters(TriggerTrafficScope.AllTime, 0)), "trigger.condition.threshold.invalid" },
        { new TriggerCondition("window", TriggerConditionKind.Traffic, new TrafficConditionParameters(TriggerTrafficScope.RollingWindow, 1)), "trigger.condition.window.invalid" },
        { new TriggerCondition("unexpected-window", TriggerConditionKind.Traffic, new TrafficConditionParameters(TriggerTrafficScope.CurrentSession, 1, TimeSpan.FromMinutes(5))), "trigger.condition.window.unexpected" },
        { new TriggerCondition("direction", TriggerConditionKind.Rate, new RateConditionParameters((TriggerTrafficDirection)999, 1)), "trigger.condition.rate.direction.undefined" },
        { new TriggerCondition("rate", TriggerConditionKind.Rate, new RateConditionParameters(TriggerTrafficDirection.Download, -1)), "trigger.condition.threshold.invalid" },
        { new TriggerCondition("connections", TriggerConditionKind.ActiveConnections, new ActiveConnectionsConditionParameters(0)), "trigger.condition.threshold.invalid" },
        { new TriggerCondition("runtime", TriggerConditionKind.Runtime, new RuntimeConditionParameters(TimeSpan.Zero)), "trigger.condition.threshold.invalid" },
        { new TriggerCondition("notification", TriggerConditionKind.Notification, new NotificationConditionParameters((TriggerNotificationLevel)999)), "trigger.condition.notification.level.undefined" },
    };

    public static TheoryData<TriggerAction, string> InvalidActions => new()
    {
        { new TriggerAction((TriggerActionKind)999, new NoActionParameters()), "trigger.action.kind.undefined" },
        { new TriggerAction(TriggerActionKind.SetLaunchAtStartup, new NoActionParameters()), "trigger.action.parameters.mismatch" },
        { new TriggerAction(TriggerActionKind.SwitchProxyMode, new ProxyModeActionParameters((ClashSharpMode)999)), "trigger.action.mode.undefined" },
        { new TriggerAction(TriggerActionKind.SendNotification, new NotificationActionParameters(" ")), "trigger.action.notification.message.required" },
    };

    private static TriggerTaskDefinition ValidDefinition(
        IReadOnlyList<TriggerCondition>? conditions = null,
        IReadOnlyList<TriggerAction>? actions = null)
    {
        return new TriggerTaskDefinition(
            "task",
            1,
            "Task",
            true,
            conditions ?? [EventCondition("entered", TriggerEventKind.AppEntered)],
            actions ?? [NotificationAction("hello")]);
    }

    private static TriggerCondition EventCondition(string id, TriggerEventKind eventKind)
    {
        return new TriggerCondition(
            id,
            TriggerConditionKind.Event,
            new EventConditionParameters(eventKind));
    }

    private static TriggerAction NotificationAction(string message)
    {
        return new TriggerAction(
            TriggerActionKind.SendNotification,
            new NotificationActionParameters(message));
    }
}
