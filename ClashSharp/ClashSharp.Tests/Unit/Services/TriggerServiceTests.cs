using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests the temporary legacy CRUD facade retained until the editor migration completes.</summary>
public sealed class TriggerServiceTests
{
    [Fact]
    public void GetTasks_ReturnsDefensiveCopiesThatCannotMutateStoredTasks()
    {
        TriggerService service = CreateService(CreateTempStoragePath());
        service.SaveTasks(
        [
            new TriggerTask(
                "defensive",
                "Original",
                true,
                [new TriggerCondition(TriggerConditionKind.AppEntered)],
                [new TriggerAction(TriggerActionKind.SendNotification, "original")]),
        ]);

        TriggerTask task = Assert.Single(service.GetTasks());
        task.Name = "Mutated";
        task.IsEnabled = false;
        task.Conditions = [new TriggerCondition(TriggerConditionKind.Runtime, Threshold: 60)];
        task.Actions = [new TriggerAction(TriggerActionKind.ExitApplication)];
        task.LastTriggeredAt = DateTimeOffset.UnixEpoch;

        TriggerTask storedTask = Assert.Single(service.GetTasks());
        Assert.Equal("Original", storedTask.Name);
        Assert.True(storedTask.IsEnabled);
        Assert.Equal(TriggerConditionKind.AppEntered, Assert.Single(storedTask.Conditions).Kind);
        Assert.Equal(TriggerActionKind.SendNotification, Assert.Single(storedTask.Actions).Kind);
        Assert.Null(storedTask.LastTriggeredAt);
    }

    [Fact]
    public void SaveTasks_CopiesInputTasksBeforeStoring()
    {
        TriggerService service = CreateService(CreateTempStoragePath());
        TriggerTask inputTask = new(
            "input-copy",
            "Input copy",
            true,
            [new TriggerCondition(TriggerConditionKind.AppEntered)],
            [new TriggerAction(TriggerActionKind.SendNotification, "original")]);

        service.SaveTasks([inputTask]);
        inputTask.Name = "Mutated";
        inputTask.IsEnabled = false;
        inputTask.Actions = [new TriggerAction(TriggerActionKind.ExitApplication)];

        TriggerTask storedTask = Assert.Single(service.GetTasks());
        Assert.Equal("Input copy", storedTask.Name);
        Assert.True(storedTask.IsEnabled);
        Assert.Equal([TriggerActionKind.SendNotification], storedTask.Actions.Select(static action => action.Kind));
    }

    [Fact]
    public async Task EvaluateAsync_WhenOneTriggerActionFails_ContinuesWithOtherTriggers()
    {
        FakeTriggerActions actions = new()
        {
            ThrowForValue = "fail",
        };
        FakeTriggerLog log = new();
        TriggerService service = CreateService(CreateTempStoragePath(), actions, log);
        service.SaveTasks(
        [
            new TriggerTask(
                "failing",
                "Failing trigger",
                true,
                [new TriggerCondition(TriggerConditionKind.AppEntered)],
                [new TriggerAction(TriggerActionKind.SendNotification, "fail")]),
            new TriggerTask(
                "passing",
                "Passing trigger",
                true,
                [new TriggerCondition(TriggerConditionKind.AppEntered)],
                [new TriggerAction(TriggerActionKind.SendNotification, "pass")]),
        ]);

        IReadOnlyList<TriggerExecutionResult> results = await service.EvaluateAsync(
            AppEnteredContext(),
            CancellationToken.None);

        Assert.Equal("passing", Assert.Single(results).TaskId);
        Assert.Equal(["fail", "pass"], actions.DispatchValues);
        Assert.Contains(log.Entries, entry => entry.Level == "Warning" && entry.Category == "Trigger");
    }

    [Fact]
    public async Task EvaluateAsync_WhenTriggersDisabledAtRuntime_DoesNotDispatchMatchingTask()
    {
        FakeTriggerActions actions = new();
        bool triggersEnabled = true;
        TriggerService service = CreateService(
            CreateTempStoragePath(),
            actions,
            getTriggersEnabled: () => triggersEnabled,
            setTriggersEnabled: value => triggersEnabled = value);
        service.SaveTasks(
        [
            new TriggerTask(
                "disabled",
                "Disabled trigger",
                true,
                [new TriggerCondition(TriggerConditionKind.AppEntered)],
                [new TriggerAction(TriggerActionKind.SendNotification, "disabled")]),
        ]);

        service.TriggersEnabled = false;
        IReadOnlyList<TriggerExecutionResult> results = await service.EvaluateAsync(
            AppEnteredContext(),
            CancellationToken.None);

        Assert.False(triggersEnabled);
        Assert.Empty(results);
        Assert.Empty(actions.DispatchValues);
    }

    private static TriggerService CreateService(
        string storagePath,
        FakeTriggerActions? actions = null,
        FakeTriggerLog? log = null,
        Func<bool>? getTriggersEnabled = null,
        Action<bool>? setTriggersEnabled = null)
    {
        log ??= new FakeTriggerLog();
        return new TriggerService(
            storagePath,
            actions ?? new FakeTriggerActions(),
            new FakeTriggerNotifications(),
            log.Append,
            key => key switch
            {
                "Triggers.Log.Fired.Format" => "Trigger fired: {0}",
                "Triggers.Log.ActionFailed.Format" => "Trigger action failed: {0}",
                _ => key,
            },
            getTriggersEnabled,
            setTriggersEnabled);
    }

    private static TriggerEvaluationContext AppEnteredContext() => new(
        TriggerEventKind.AppEntered,
        0,
        0,
        TimeSpan.Zero,
        TimeOnly.MinValue,
        NotificationLevel.Default);

    private static string CreateTempStoragePath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ClashSharpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "Triggers.json");
    }

    private sealed class FakeTriggerActions : IApplicationActionDispatcher
    {
        public List<string> DispatchValues { get; } = [];

        public string? ThrowForValue { get; init; }

        public Task DispatchAsync(
            ApplicationActionKind kind,
            string value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DispatchValues.Add(value);
            return StringComparer.Ordinal.Equals(value, ThrowForValue)
                ? Task.FromException(new InvalidOperationException("action failed"))
                : Task.CompletedTask;
        }
    }

    private sealed class FakeTriggerNotifications : ITriggerNotificationSink
    {
        public void NotifyTriggerFired(string triggerName)
        {
        }
    }

    private sealed class FakeTriggerLog
    {
        public List<LogEntry> Entries { get; } = [];

        public void Append(string level, string category, string message, string? detail)
        {
            Entries.Add(new LogEntry(level, category, message, detail));
        }
    }

    private sealed record LogEntry(string Level, string Category, string Message, string? Detail);
}
