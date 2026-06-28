/*
 * Trigger Service
 * Stores, orders, evaluates, and executes user-defined trigger tasks
 *
 * @author: WaterRun
 * @file: Service/TriggerService.cs
 * @date: 2026-06-26
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Notification commands required by trigger execution.</summary>
internal interface ITriggerNotificationSink
{
    /// <summary>Sends a notification after one trigger task fires.</summary>
    void NotifyTriggerFired(string triggerName);
}

/// <summary>Persistent trigger task service.</summary>
internal sealed class TriggerService
{
    private const string TriggerLog = "Trigger";
    private static readonly TimeSpan DefaultPeriodicInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRepeatedTriggerCooldown = TimeSpan.FromMinutes(5);

#if UNIT_TESTS
    public static TriggerService Instance => throw new NotSupportedException("Use explicit TriggerService dependencies in tests.");
#else
    public static TriggerService Instance { get; } = CreateDefault();
#endif

    private readonly string _storagePath;
    private readonly IApplicationActionDispatcher _actions;
    private readonly ITriggerNotificationSink _notifications;
    private readonly ITriggerRuntimeEventSource _runtimeEvents;
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Func<string, string> _getString;
    private readonly Func<TriggerRuntimeEvent, TriggerEvaluationContext> _createEvaluationContext;
    private readonly Func<bool> _getTriggersEnabled;
    private readonly Action<bool> _setTriggersEnabled;
    private readonly Func<bool> _getTriggerNotificationsEnabled;
    private readonly Func<TriggerEvaluationContext> _createPeriodicContext;
    private readonly Func<DateTimeOffset> _getNow;
    private readonly TimeSpan _periodicInterval;
    private readonly TimeSpan _repeatedTriggerCooldown;
    private readonly object _syncLock = new();
    private List<TriggerTask> _tasks = [];
    private readonly ConcurrentQueue<TriggerRuntimeEvent> _pendingRuntimeEvents = new();
    private Timer? _periodicTimer;
    private bool _periodicStartRequested;
    private int _runtimeEventDrainActive;
    private int _periodicEvaluationActive;
    private int _triggerGeneratedNotificationSuppressionDepth;

    public TriggerService(
        string storagePath,
        IApplicationActionDispatcher actions,
        ITriggerNotificationSink notifications,
        ITriggerRuntimeEventSource runtimeEvents,
        Action<string, string, string, string?> appendLog,
        Func<string, string>? getString = null,
        Func<TriggerRuntimeEvent, TriggerEvaluationContext>? createEvaluationContext = null,
        Func<bool>? getTriggersEnabled = null,
        Action<bool>? setTriggersEnabled = null,
        Func<bool>? getTriggerNotificationsEnabled = null,
        TimeSpan? periodicInterval = null,
        Func<TriggerEvaluationContext>? createPeriodicContext = null,
        TimeSpan? repeatedTriggerCooldown = null,
        Func<DateTimeOffset>? getNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        _storagePath = Path.GetFullPath(storagePath);
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _runtimeEvents = runtimeEvents ?? throw new ArgumentNullException(nameof(runtimeEvents));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _getString = getString ?? (key => key);
        _createEvaluationContext = createEvaluationContext
            ?? (triggerEvent => TriggerEvaluationContextFactory.Create(triggerEvent.EventKind, triggerEvent.NotificationLevel));
        _getTriggersEnabled = getTriggersEnabled ?? (() => true);
        _setTriggersEnabled = setTriggersEnabled ?? (_ => { });
        _getTriggerNotificationsEnabled = getTriggerNotificationsEnabled ?? (() => true);
        _periodicInterval = periodicInterval ?? DefaultPeriodicInterval;
        _createPeriodicContext = createPeriodicContext ?? (() => TriggerEvaluationContextFactory.Create(TriggerEventKind.Periodic));
        _repeatedTriggerCooldown = repeatedTriggerCooldown ?? DefaultRepeatedTriggerCooldown;
        _getNow = getNow ?? (() => DateTimeOffset.Now);
        _runtimeEvents.RuntimeEventRaised += OnRuntimeEventRaised;
        Load();
    }

    /// <summary>Ensures the singleton instance has been constructed and subscribed to runtime events.</summary>
    public void Start()
    {
        lock (_syncLock)
        {
            _periodicStartRequested = true;
        }

        StartPeriodicTimerIfEnabled();
    }

    /// <summary>Stops periodic trigger evaluation for this service instance.</summary>
    public void Stop()
    {
        StopPeriodicTimer(keepStartRequested: false);
    }

    public bool TriggersEnabled
    {
        get => _getTriggersEnabled();
        set
        {
            _setTriggersEnabled(value);
            if (value)
            {
                StartPeriodicTimerIfEnabled();
            }
            else
            {
                StopPeriodicTimer(keepStartRequested: true);
            }
        }
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

        _appendLog("Info", TriggerLog, GetString("Triggers.Log.Saved"), $"{tasks.Count} task(s)");
    }

    public void AddTask(TriggerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_syncLock)
        {
            _tasks.Add(task);
            Save();
        }

        _appendLog("Info", TriggerLog, string.Format(GetString("Triggers.Log.Added.Format"), task.Name), task.Id);
    }

    public void DeleteTask(string id)
    {
        string? deletedName = null;
        lock (_syncLock)
        {
            deletedName = _tasks.FirstOrDefault(task => StringComparer.Ordinal.Equals(task.Id, id))?.Name;
            _tasks.RemoveAll(task => StringComparer.Ordinal.Equals(task.Id, id));
            Save();
        }

        if (deletedName is not null)
        {
            _appendLog("Info", TriggerLog, string.Format(GetString("Triggers.Log.Deleted.Format"), deletedName), id);
        }
    }

    public void MoveTask(string id, int direction)
    {
        string? movedName = null;
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
            movedName = task.Name;
            Save();
        }

        if (movedName is not null)
        {
            _appendLog("Info", TriggerLog, string.Format(GetString("Triggers.Log.Moved.Format"), movedName), direction.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

        _appendLog("Info", TriggerLog, GetString(isEnabled ? "Triggers.Log.EnabledAll" : "Triggers.Log.DisabledAll"), null);
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

            DateTimeOffset triggeredAt = _getNow();
            if (IsPeriodicCooldownActive(task, context, triggeredAt))
            {
                continue;
            }

            bool taskFailed = false;
            foreach (TriggerAction action in task.Actions)
            {
                try
                {
                    await ExecuteActionAsync(action, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    taskFailed = true;
                    AppendActionFailureLog(task, action, exception);
                    break;
                }
            }

            if (taskFailed)
            {
                continue;
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
                try
                {
                    Interlocked.Increment(ref _triggerGeneratedNotificationSuppressionDepth);
                    _notifications.NotifyTriggerFired(task.Name);
                }
                finally
                {
                    Interlocked.Decrement(ref _triggerGeneratedNotificationSuppressionDepth);
                }
            }
        }

        if (results.Count > 0)
        {
            PersistCurrentTasks();
        }

        return results;
    }

    private void PersistCurrentTasks()
    {
        lock (_syncLock)
        {
            Save();
        }
    }

#if !UNIT_TESTS
    private static TriggerService CreateDefault()
    {
        return new TriggerService(
            Path.Combine(AppDataPathService.ResolveLocalDataDirectory(), "Triggers.json"),
            ApplicationActionService.Instance,
            NotificationService.Instance,
            TriggerRuntimeEventHub.Instance,
            LogStorageService.Instance.AppendLog,
            LocalizationService.Instance.GetString,
            triggerEvent => TriggerEvaluationContextFactory.Create(triggerEvent.EventKind, triggerEvent.NotificationLevel),
            () => AppSettingsService.Instance.TriggersEnabled,
            value => AppSettingsService.Instance.TriggersEnabled = value,
            () => AppSettingsService.Instance.TriggerNotificationsEnabled);
    }
#endif

    private string FormatActionForLog(TriggerAction action)
    {
        return _getString($"Triggers.Action.{action.Kind}");
    }

    private string GetString(string key)
    {
        return _getString(key);
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

        return ShouldSuppressNotificationEventsDuringAction(action.Kind)
            ? ExecuteActionWithNotificationSuppressionAsync(kind, action.Value, cancellationToken)
            : _actions.DispatchAsync(kind, action.Value, cancellationToken);
    }

    private void OnRuntimeEventRaised(object? sender, TriggerRuntimeEvent triggerEvent)
    {
        if (triggerEvent.EventKind == TriggerEventKind.NotificationRaised
            && Volatile.Read(ref _triggerGeneratedNotificationSuppressionDepth) > 0)
        {
            return;
        }

        _pendingRuntimeEvents.Enqueue(triggerEvent);
        if (Interlocked.Exchange(ref _runtimeEventDrainActive, 1) == 1)
        {
            return;
        }

        _ = DrainRuntimeEventsAsync();
    }

    private async Task DrainRuntimeEventsAsync()
    {
        try
        {
            while (_pendingRuntimeEvents.TryDequeue(out TriggerRuntimeEvent? triggerEvent))
            {
                try
                {
                    TriggerEvaluationContext context = _createEvaluationContext(triggerEvent);
                    await EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _appendLog("Warning", TriggerLog, GetString("Triggers.Log.RuntimeEventFailed"), exception.Message);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _runtimeEventDrainActive, 0);
            if (!_pendingRuntimeEvents.IsEmpty
                && Interlocked.Exchange(ref _runtimeEventDrainActive, 1) == 0)
            {
                _ = DrainRuntimeEventsAsync();
            }
        }
    }

    private void OnPeriodicTimer(object? state)
    {
        if (!TriggersEnabled || Interlocked.Exchange(ref _periodicEvaluationActive, 1) == 1)
        {
            return;
        }

        _ = EvaluatePeriodicAsync();
    }

    private void StartPeriodicTimerIfEnabled()
    {
        if (!TriggersEnabled || _periodicInterval <= TimeSpan.Zero)
        {
            return;
        }

        lock (_syncLock)
        {
            if (!_periodicStartRequested)
            {
                return;
            }

            _periodicTimer ??= new Timer(OnPeriodicTimer, null, _periodicInterval, _periodicInterval);
        }
    }

    private void StopPeriodicTimer(bool keepStartRequested)
    {
        Timer? timer;
        lock (_syncLock)
        {
            if (!keepStartRequested)
            {
                _periodicStartRequested = false;
            }

            timer = _periodicTimer;
            _periodicTimer = null;
        }

        timer?.Dispose();
    }

    private async Task EvaluatePeriodicAsync()
    {
        try
        {
            TriggerEvaluationContext context = _createPeriodicContext();
            await EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _appendLog("Warning", TriggerLog, GetString("Triggers.Log.RuntimeEventFailed"), exception.Message);
        }
        finally
        {
            Volatile.Write(ref _periodicEvaluationActive, 0);
        }
    }

    private async Task ExecuteActionWithNotificationSuppressionAsync(
        ApplicationActionKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        try
        {
            Interlocked.Increment(ref _triggerGeneratedNotificationSuppressionDepth);
            await _actions.DispatchAsync(kind, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _triggerGeneratedNotificationSuppressionDepth);
        }
    }

    private static bool ShouldSuppressNotificationEventsDuringAction(TriggerActionKind kind)
    {
        return kind is TriggerActionKind.SendNotification or TriggerActionKind.SwitchProxyMode;
    }

    private void AppendActionFailureLog(TriggerTask task, TriggerAction action, Exception exception)
    {
        _appendLog(
            "Warning",
            TriggerLog,
            string.Format(GetString("Triggers.Log.ActionFailed.Format"), task.Name),
            $"{FormatActionForLog(action)}: {exception.Message}");
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

    private bool IsPeriodicCooldownActive(TriggerTask task, TriggerEvaluationContext context, DateTimeOffset now)
    {
        return context.EventKind == TriggerEventKind.Periodic
            && _repeatedTriggerCooldown > TimeSpan.Zero
            && task.LastTriggeredAt is DateTimeOffset lastTriggeredAt
            && now - lastTriggeredAt < _repeatedTriggerCooldown;
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
