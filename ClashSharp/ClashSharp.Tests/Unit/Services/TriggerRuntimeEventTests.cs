/*
 * Trigger Runtime Event Tests
 * Verifies the in-process runtime event boundary used by trigger evaluation
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/TriggerRuntimeEventTests.cs
 * @date: 2026-06-28
 */

extern alias ClashSharpUi;

using ClashSharp.Model.Triggers;
using NotificationLevel = ClashSharp.Model.NotificationLevel;
using TriggerRuntimeEvent = ClashSharpUi::ClashSharp.Service.TriggerRuntimeEvent;
using TriggerRuntimeEventHub = ClashSharpUi::ClashSharp.Service.TriggerRuntimeEventHub;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for trigger runtime event publishing.</summary>
public sealed class TriggerRuntimeEventTests
{
    /// <summary>Verifies the event hub delivers published runtime events to subscribers.</summary>
    [Fact]
    public void Publish_RaisesRuntimeEvent()
    {
        TriggerRuntimeEventHub hub = new();
        List<TriggerRuntimeEvent> events = [];
        hub.RuntimeEventRaised += (_, triggerEvent) => events.Add(triggerEvent);

        hub.Publish(new TriggerRuntimeEvent(TriggerEventKind.NotificationRaised, NotificationLevel.CriticalOnly));

        TriggerRuntimeEvent triggerEvent = Assert.Single(events);
        Assert.Equal(TriggerEventKind.NotificationRaised, triggerEvent.EventKind);
        Assert.Equal(NotificationLevel.CriticalOnly, triggerEvent.NotificationLevel);
    }
}
