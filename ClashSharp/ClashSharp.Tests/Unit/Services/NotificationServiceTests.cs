/*
 * Notification Service Tests
 * Verifies Win11 notification policy, logging, and trigger runtime event publishing through an injected gateway
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/NotificationServiceTests.cs
 * @date: 2026-06-28
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for notification policy and logging behavior.</summary>
public sealed class NotificationServiceTests
{
    [Fact]
    public void Show_WhenNotificationIsSuppressed_LogsSuppressionWithoutPlatformCallOrRuntimeEvent()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerRuntimeEvents triggerEvents = new();
        FakeNotificationLog log = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.CriticalOnly,
            platform,
            triggerEvents,
            log);

        service.Show(NotificationLevel.Default, "Title", "Message");

        Assert.Empty(platform.Requests);
        Assert.Empty(triggerEvents.Events);
        LogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Info", entry.Level);
        Assert.Equal("Notification", entry.Category);
        Assert.Equal("Suppressed Title Message", entry.Message);
        Assert.Contains("Title: Title", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_WhenNotificationIsShown_LogsSuccessAndPublishesNotificationRaisedEvent()
    {
        FakeWin11NotificationPlatform platform = new();
        FakeTriggerRuntimeEvents triggerEvents = new();
        FakeNotificationLog log = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.Default,
            platform,
            triggerEvents,
            log);

        service.Show(NotificationLevel.CriticalOnly, "Critical", "Body");

        Assert.Equal([new NotificationRequest("Critical", "Body")], platform.Requests);
        TriggerRuntimeEvent triggerEvent = Assert.Single(triggerEvents.Events);
        Assert.Equal(TriggerEventKind.NotificationRaised, triggerEvent.EventKind);
        Assert.Equal(NotificationLevel.CriticalOnly, triggerEvent.NotificationLevel);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Message == "Shown Critical Body");
    }

    [Fact]
    public void Show_WhenWin11NotificationFails_LogsWarningWithoutPublishingRuntimeEvent()
    {
        FakeWin11NotificationPlatform platform = new()
        {
            ExceptionToThrow = new InvalidOperationException("toast unavailable"),
        };
        FakeTriggerRuntimeEvents triggerEvents = new();
        FakeNotificationLog log = new();
        NotificationService service = CreateService(
            enabled: true,
            configuredLevel: NotificationLevel.More,
            platform,
            triggerEvents,
            log);

        service.Show(NotificationLevel.Default, "Title", "Message");

        Assert.Empty(triggerEvents.Events);
        LogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("Failed Title Message toast unavailable", entry.Message);
        Assert.Contains("Error: toast unavailable", entry.Detail, StringComparison.Ordinal);
    }

    private static NotificationService CreateService(
        bool enabled,
        NotificationLevel configuredLevel,
        FakeWin11NotificationPlatform platform,
        FakeTriggerRuntimeEvents triggerEvents,
        FakeNotificationLog log)
    {
        return new NotificationService(
            () => enabled,
            () => configuredLevel,
            key => key switch
            {
                "Notification.Log.Suppressed" => "Suppressed {0} {1}",
                "Notification.Log.Shown" => "Shown {0} {1}",
                "Notification.Log.Failed" => "Failed {0} {1} {2}",
                "Notification.Custom.Title" => "Custom",
                "Notification.Custom.Message" => "Default message",
                "Notification.ProxyMode.Title" => "Proxy mode",
                "Notification.ProxyMode.Message.Format" => "Mode {0}",
                "Notification.TriggerFired.Title" => "Trigger",
                "Notification.TriggerFired.Message.Format" => "Trigger {0}",
                "Notification.ConnectionTestTimeout.Title" => "Timeout",
                "Notification.ConnectionTestTimeout.Message.Format" => "Timeout {0}",
                "Master.Mode.Standby.Title" => "Standby",
                "Master.Mode.RuleTakeover.Title" => "Rule",
                "Master.Mode.FullTakeover.Title" => "Full",
                "Master.Mode.Disabled.Title" => "Disabled",
                _ => key,
            },
            log.Append,
            triggerEvents,
            platform);
    }

    private sealed class FakeWin11NotificationPlatform : IWin11NotificationPlatform
    {
        public List<NotificationRequest> Requests { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public void Show(string title, string message)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            Requests.Add(new NotificationRequest(title, message));
        }
    }

    private sealed class FakeTriggerRuntimeEvents : ITriggerRuntimeEventPublisher
    {
        public List<TriggerRuntimeEvent> Events { get; } = [];

        public void Publish(TriggerRuntimeEvent triggerEvent)
        {
            Events.Add(triggerEvent);
        }
    }

    private sealed class FakeNotificationLog
    {
        public List<LogEntry> Entries { get; } = [];

        public void Append(string level, string category, string message, string? detail)
        {
            Entries.Add(new LogEntry(level, category, message, detail ?? string.Empty));
        }
    }

    private sealed record NotificationRequest(string Title, string Message);

    private sealed record LogEntry(string Level, string Category, string Message, string Detail);
}
