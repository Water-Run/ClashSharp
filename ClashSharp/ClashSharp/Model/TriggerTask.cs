/*
 * Trigger Task Models
 * Defines trigger conditions, actions, and ordered tasks
 *
 * @author: WaterRun
 * @file: Model/TriggerTask.cs
 * @date: 2026-06-26
 */

using System;
using System.Collections.Generic;

namespace ClashSharp.Model;

/// <summary>Event currently being evaluated by the trigger service.</summary>
internal enum TriggerEventKind
{
    Periodic,
    AppEntered,
    ProxyStarted,
    NotificationRaised,
}

/// <summary>Condition types supported by trigger tasks.</summary>
internal enum TriggerConditionKind
{
    AppEntered,
    ProxyStarted,
    NotificationRaised,
    TotalTraffic,
    TrafficInWindow,
    UploadRate,
    DownloadRate,
    ActiveConnections,
    SessionTraffic,
    Runtime,
    SystemTime,
}

/// <summary>Action types supported by trigger tasks.</summary>
internal enum TriggerActionKind
{
    CloseConnections,
    SetLaunchAtStartup,
    SetTransparentProxy,
    SetConnectionSampling,
    SwitchProxyMode,
    ExitApplication,
    SendNotification,
}

/// <summary>Runtime context used to evaluate trigger conditions.</summary>
internal sealed record TriggerEvaluationContext(
    TriggerEventKind EventKind,
    long TotalTrafficBytes,
    long WindowTrafficBytes,
    TimeSpan Runtime,
    TimeOnly SystemTime,
    NotificationLevel NotificationLevel,
    long UploadBytesPerSecond = 0,
    long DownloadBytesPerSecond = 0,
    int ActiveConnectionCount = 0,
    long SessionTrafficBytes = 0);

/// <summary>One trigger condition with a kind and optional scalar value.</summary>
internal sealed record TriggerCondition(
    TriggerConditionKind Kind,
    long Threshold = 0,
    string Value = "");

/// <summary>One trigger action with a kind and optional scalar value.</summary>
internal sealed record TriggerAction(
    TriggerActionKind Kind,
    string Value = "");

/// <summary>Ordered trigger task containing all conditions and actions to run.</summary>
internal sealed class TriggerTask
{
    public TriggerTask()
    {
    }

    public TriggerTask(
        string id,
        string name,
        bool isEnabled,
        IReadOnlyList<TriggerCondition> conditions,
        IReadOnlyList<TriggerAction> actions,
        DateTimeOffset? lastTriggeredAt = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        Name = string.IsNullOrWhiteSpace(name) ? "Trigger" : name.Trim();
        IsEnabled = isEnabled;
        Conditions = conditions ?? [];
        Actions = actions ?? [];
        LastTriggeredAt = lastTriggeredAt;
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Trigger";

    public bool IsEnabled { get; set; }

    public IReadOnlyList<TriggerCondition> Conditions { get; set; } = [];

    public IReadOnlyList<TriggerAction> Actions { get; set; } = [];

    public DateTimeOffset? LastTriggeredAt { get; set; }
}

/// <summary>Describes one fired trigger after action execution.</summary>
internal sealed record TriggerExecutionResult(
    string TaskId,
    string TaskName,
    DateTimeOffset TriggeredAt,
    IReadOnlyList<TriggerAction> Actions);
