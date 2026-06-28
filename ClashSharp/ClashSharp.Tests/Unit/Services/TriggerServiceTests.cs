/*
 * Trigger Service Tests
 * Verifies trigger evaluation, runtime event dispatch, and failure isolation
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/TriggerServiceTests.cs
 * @date: 2026-06-28
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for trigger task evaluation and runtime event dispatch.</summary>
public sealed class TriggerServiceTests
{
    /// <summary>Verifies runtime events raised during an active evaluation are queued instead of dropped.</summary>
    [Fact]
    public async Task RuntimeEvents_WhenEvaluationIsActive_AreQueuedAndEvaluated()
    {
        string storagePath = CreateTempStoragePath();
        FakeTriggerActions actions = new() { BlockFirstDispatch = true };
        FakeTriggerRuntimeEvents runtimeEvents = new();
        TriggerService service = CreateService(storagePath, actions, runtimeEvents);
        service.SaveTasks(
        [
            new TriggerTask(
                "queued",
                "Queued trigger",
                true,
                [new TriggerCondition(TriggerConditionKind.AppEntered)],
                [new TriggerAction(TriggerActionKind.SendNotification, "queued")]),
        ]);

        runtimeEvents.Publish(new TriggerRuntimeEvent(TriggerEventKind.AppEntered));
        await actions.FirstDispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        runtimeEvents.Publish(new TriggerRuntimeEvent(TriggerEventKind.AppEntered));
        actions.ReleaseFirstDispatch.TrySetResult(null);

        await WaitUntilAsync(() => actions.DispatchValues.Count == 2);
        Assert.Equal(["queued", "queued"], actions.DispatchValues);
    }

    /// <summary>Verifies one failing trigger action is logged and does not prevent later matching triggers from running.</summary>
    [Fact]
    public async Task EvaluateAsync_WhenOneTriggerActionFails_ContinuesWithOtherTriggers()
    {
        string storagePath = CreateTempStoragePath();
        FakeTriggerActions actions = new()
        {
            ThrowForValue = "fail",
        };
        FakeTriggerLog log = new();
        TriggerService service = CreateService(storagePath, actions, log: log);
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
            new TriggerEvaluationContext(
                TriggerEventKind.AppEntered,
                0,
                0,
                TimeSpan.Zero,
                TimeOnly.MinValue,
                NotificationLevel.Default),
            CancellationToken.None);

        TriggerExecutionResult result = Assert.Single(results);
        Assert.Equal("passing", result.TaskId);
        Assert.Equal(["fail", "pass"], actions.DispatchValues);
        Assert.Contains(log.Entries, entry => entry.Level == "Warning" && entry.Category == "Trigger");
    }

    private static TriggerService CreateService(
        string storagePath,
        FakeTriggerActions? actions = null,
        FakeTriggerRuntimeEvents? runtimeEvents = null,
        FakeTriggerLog? log = null)
    {
        log ??= new FakeTriggerLog();
        return new TriggerService(
            storagePath,
            actions ?? new FakeTriggerActions(),
            new FakeTriggerNotifications(),
            runtimeEvents ?? new FakeTriggerRuntimeEvents(),
            log.Append,
            key => key switch
            {
                "Triggers.Log.Fired.Format" => "Trigger fired: {0}",
                "Triggers.Log.ActionFailed.Format" => "Trigger action failed: {0}",
                _ => key,
            },
            triggerEvent => new TriggerEvaluationContext(
                triggerEvent.EventKind,
                0,
                0,
                TimeSpan.Zero,
                TimeOnly.MinValue,
                triggerEvent.NotificationLevel));
    }

    private static string CreateTempStoragePath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ClashSharpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "Triggers.json");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeTriggerActions : IApplicationActionDispatcher
    {
        private int _dispatchCount;

        public List<string> DispatchValues { get; } = [];

        public bool BlockFirstDispatch { get; init; }

        public string? ThrowForValue { get; init; }

        public TaskCompletionSource<string> FirstDispatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> ReleaseFirstDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DispatchAsync(ApplicationActionKind kind, string value, CancellationToken cancellationToken)
        {
            DispatchValues.Add(value);
            int dispatchCount = Interlocked.Increment(ref _dispatchCount);
            if (dispatchCount == 1)
            {
                FirstDispatchStarted.TrySetResult(value);
                if (BlockFirstDispatch)
                {
                    await ReleaseFirstDispatch.Task.WaitAsync(cancellationToken);
                }
            }

            if (StringComparer.Ordinal.Equals(value, ThrowForValue))
            {
                throw new InvalidOperationException("action failed");
            }
        }
    }

    private sealed class FakeTriggerNotifications : ITriggerNotificationSink
    {
        public void NotifyTriggerFired(string triggerName)
        {
        }
    }

    private sealed class FakeTriggerRuntimeEvents : ITriggerRuntimeEventSource
    {
        public event EventHandler<TriggerRuntimeEvent>? RuntimeEventRaised;

        public void Publish(TriggerRuntimeEvent triggerEvent)
        {
            RuntimeEventRaised?.Invoke(this, triggerEvent);
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
