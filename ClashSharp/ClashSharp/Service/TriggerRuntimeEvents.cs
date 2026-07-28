using System;
using ClashSharp.Model;
using TriggerEventKind = global::ClashSharp.Model.Triggers.TriggerEventKind;

namespace ClashSharp.Service;

/// <summary>Source of runtime events that can be evaluated by trigger tasks.</summary>
internal interface ITriggerRuntimeEventSource
{
    /// <summary>Raised when a runtime event relevant to trigger evaluation occurs.</summary>
    event EventHandler<TriggerRuntimeEvent>? RuntimeEventRaised;
}

/// <summary>Publishes runtime events without depending on the trigger service.</summary>
internal interface ITriggerRuntimeEventPublisher
{
    /// <summary>Publishes one runtime event to subscribers.</summary>
    void Publish(TriggerRuntimeEvent triggerEvent);
}

/// <summary>In-process runtime event hub shared by action and notification services.</summary>
internal sealed class TriggerRuntimeEventHub : ITriggerRuntimeEventSource, ITriggerRuntimeEventPublisher
{
    public static TriggerRuntimeEventHub Instance { get; } = new();

    internal TriggerRuntimeEventHub()
    {
    }

    public event EventHandler<TriggerRuntimeEvent>? RuntimeEventRaised;

    public void Publish(TriggerRuntimeEvent triggerEvent)
    {
        ArgumentNullException.ThrowIfNull(triggerEvent);
        RuntimeEventRaised?.Invoke(this, triggerEvent);
    }
}

/// <summary>Runtime event data used to build a trigger evaluation context.</summary>
internal sealed record TriggerRuntimeEvent(
    TriggerEventKind EventKind,
    NotificationLevel NotificationLevel = NotificationLevel.Default);
