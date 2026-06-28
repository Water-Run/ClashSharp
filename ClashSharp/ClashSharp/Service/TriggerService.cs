/*
 * Trigger Service
 * Stores, orders, evaluates, and executes user-defined trigger tasks
 *
 * @author: WaterRun
 * @file: Service/TriggerService.cs
 * @date: 2026-06-26
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Persistent trigger task service.</summary>
internal sealed class TriggerService
{
    private const string TriggerLog = "Trigger";

    public static TriggerService Instance { get; } = CreateDefault();

    private readonly string _storagePath;
    private readonly IApplicationActionDispatcher _actions;
    private readonly NotificationService _notifications;
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Func<string, string> _getString;
    private readonly Func<bool> _getTriggersEnabled;
    private readonly Action<bool> _setTriggersEnabled;
    private readonly Func<bool> _getTriggerNotificationsEnabled;
    private readonly object _syncLock = new();
    private List<TriggerTask> _tasks = [];
    private int _notificationEvaluationActive;

    public TriggerService(
        string storagePath,
        IApplicationActionDispatcher actions,
        NotificationService notifications,
        Action<string, string, string, string?> appendLog,
        Func<string, string>? getString = null,
        Func<bool>? getTriggersEnabled = null,
        Action<bool>? setTriggersEnabled = null,
        Func<bool>? getTriggerNotificationsEnabled = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        _storagePath = Path.GetFullPath(storagePath);
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _getString = getString ?? (key => key);
        _getTriggersEnabled = getTriggersEnabled ?? (() => true);
        _setTriggersEnabled = setTriggersEnabled ?? (_ => { });
        _getTriggerNotificationsEnabled = getTriggerNotificationsEnabled ?? (() => true);
        _notifications.NotificationRaised += OnNotificationRaised;
        Load();
    }

    public bool TriggersEnabled
    {
        get => _getTriggersEnabled();
        set => _setTriggersEnabled(value);
    }

    public bool TriggerNotificationsEnabled => _getTriggerNotificationsEnabled();

    public IReadOnlyList<TriggerTask> GetTasks()
    {
        lock (_syncLock)
        {
            return [.. _tasks];
        }
    }

    public void SaveTasks(IReadOnlyList<TriggerTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_syncLock)
        {
            _tasks = [.. tasks];
            Save();
        }
    }

    public void AddTask(TriggerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_syncLock)
        {
            _tasks.Add(task);
            Save();
        }
    }

    public void DeleteTask(string id)
    {
        lock (_syncLock)
        {
            _tasks.RemoveAll(task => StringComparer.Ordinal.Equals(task.Id, id));
            Save();
        }
    }

    public void MoveTask(string id, int direction)
    {
        lock (_syncLock)
        {
            int index = _tasks.FindIndex(task => StringComparer.Ordinal.Equals(task.Id, id));
            int newIndex = index + direction;
            if (index < 0 || newIndex < 0 || newIndex >= _tasks.Count)
            {
                return;
            }

            TriggerTask task = _tasks[index];
            _tasks.RemoveAt(index);
            _tasks.Insert(newIndex, task);
            Save();
        }
    }

    public void SetAllTasksEnabled(bool isEnabled)
    {
        lock (_syncLock)
        {
            foreach (TriggerTask task in _tasks)
            {
                task.IsEnabled = isEnabled;
            }

            Save();
        }
    }

    public async Task<IReadOnlyList<TriggerExecutionResult>> EvaluateAsync(TriggerEvaluationContext context, CancellationToken cancellationToken)
    {
        if (!TriggersEnabled)
        {
            return [];
        }

        List<TriggerExecutionResult> results = [];
        foreach (TriggerTask task in GetTasks())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!task.IsEnabled || !Matches(task, context))
            {
                continue;
            }

            DateTimeOffset triggeredAt = DateTimeOffset.Now;
            foreach (TriggerAction action in task.Actions)
            {
                await ExecuteActionAsync(action, cancellationToken).ConfigureAwait(false);
            }

            task.LastTriggeredAt = triggeredAt;
            results.Add(new TriggerExecutionResult(task.Id, task.Name, triggeredAt, task.Actions));
            _appendLog(
                "Info",
                TriggerLog,
                string.Format(_getString("Triggers.Log.Fired.Format"), task.Name),
                string.Join(", ", task.Actions.Select(FormatActionForLog)));
            if (TriggerNotificationsEnabled)
            {
                _notifications.NotifyTriggerFired(task.Name);
            }
        }

        if (results.Count > 0)
        {
            SaveTasks(GetTasks());
        }

        return results;
    }

    private static TriggerService CreateDefault()
    {
        bool triggersEnabledAtStartup = AppSettingsService.Instance.TriggersEnabled;
        return new TriggerService(
            Path.Combine(AppDataPathService.ResolveLocalDataDirectory(), "Triggers.json"),
            ApplicationActionService.Instance,
            NotificationService.Instance,
            LogStorageService.Instance.AppendLog,
            LocalizationService.Instance.GetString,
            () => triggersEnabledAtStartup,
            _ => { },
            () => AppSettingsService.Instance.TriggerNotificationsEnabled);
    }

    private string FormatActionForLog(TriggerAction action)
    {
        return _getString($"Triggers.Action.{action.Kind}");
    }

    private Task ExecuteActionAsync(TriggerAction action, CancellationToken cancellationToken)
    {
        ApplicationActionKind kind = action.Kind switch
        {
            TriggerActionKind.CloseConnections => ApplicationActionKind.CloseConnections,
            TriggerActionKind.SetTransparentProxy => ApplicationActionKind.SetTransparentProxy,
            TriggerActionKind.SwitchProxyMode => ApplicationActionKind.SwitchProxyMode,
            TriggerActionKind.ExitApplication => ApplicationActionKind.ExitApplication,
            TriggerActionKind.SendNotification => ApplicationActionKind.SendNotification,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Unsupported trigger action."),
        };

        return _actions.DispatchAsync(kind, action.Value, cancellationToken);
    }

    private async void OnNotificationRaised(object? sender, NotificationRaisedEventArgs args)
    {
        if (Interlocked.Exchange(ref _notificationEvaluationActive, 1) == 1)
        {
            return;
        }

        try
        {
            TriggerEvaluationContext context = new(
                TriggerEventKind.NotificationRaised,
                TotalTrafficBytes: 0,
                WindowTrafficBytes: 0,
                Runtime: TimeSpan.Zero,
                SystemTime: TimeOnly.FromDateTime(DateTime.Now),
                args.Level);
            await EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _notificationEvaluationActive, 0);
        }
    }

    private static bool Matches(TriggerTask task, TriggerEvaluationContext context)
    {
        if (task.Conditions.Count == 0)
        {
            return false;
        }

        foreach (TriggerCondition condition in task.Conditions)
        {
            if (!Matches(condition, context))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(TriggerCondition condition, TriggerEvaluationContext context)
    {
        return condition.Kind switch
        {
            TriggerConditionKind.AppEntered => context.EventKind == TriggerEventKind.AppEntered,
            TriggerConditionKind.ProxyStarted => context.EventKind == TriggerEventKind.ProxyStarted,
            TriggerConditionKind.NotificationRaised => context.EventKind == TriggerEventKind.NotificationRaised
                && context.NotificationLevel >= ParseNotificationLevel(condition.Value),
            TriggerConditionKind.TotalTraffic => context.TotalTrafficBytes >= condition.Threshold,
            TriggerConditionKind.TrafficInWindow => context.WindowTrafficBytes >= condition.Threshold,
            TriggerConditionKind.Runtime => context.Runtime.TotalSeconds >= condition.Threshold,
            TriggerConditionKind.SystemTime => TimeOnly.TryParse(condition.Value, out TimeOnly targetTime)
                && context.SystemTime >= targetTime,
            _ => false,
        };
    }

    private static NotificationLevel ParseNotificationLevel(string value)
    {
        return Enum.TryParse(value, out NotificationLevel level) ? level : NotificationLevel.Default;
    }

    private void Load()
    {
        lock (_syncLock)
        {
            if (!File.Exists(_storagePath))
            {
                _tasks = [];
                return;
            }

            string json = File.ReadAllText(_storagePath);
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                _tasks = JsonSerializer.Deserialize<List<TriggerTask>>(json) ?? [];
                return;
            }

            TriggerStoreDocument? document = JsonSerializer.Deserialize<TriggerStoreDocument>(json);
            _tasks = document?.Tasks is null ? [] : [.. document.Tasks];
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        TriggerStoreDocument document = new(_tasks);
        File.WriteAllText(_storagePath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record TriggerStoreDocument(IReadOnlyList<TriggerTask> Tasks);
}
