using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

#if UNIT_TESTS
    public static TriggerService Instance => throw new NotSupportedException("Use explicit TriggerService dependencies in tests.");
#else
    public static TriggerService Instance => throw new NotSupportedException("Use the AppHost-owned TriggerService instance.");
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
    private int _acceptRuntimeWork = 1;
    private int _evaluationActive;

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

    /// <summary>Enables runtime-event and periodic trigger scheduling.</summary>
    public void Start()
    {
        lock (_syncLock)
        {
            Volatile.Write(ref _acceptRuntimeWork, 1);
            _periodicStartRequested = true;
        }

        StartPeriodicTimerIfEnabled();
    }

    /// <summary>Prevents new runtime-event and periodic trigger evaluation.</summary>
    public void Stop()
    {
        lock (_syncLock)
        {
            Volatile.Write(ref _acceptRuntimeWork, 0);
        }

        StopPeriodicTimer(keepStartRequested: false);
    }

    internal bool IsAcceptingRuntimeWork => Volatile.Read(ref _acceptRuntimeWork) == 1;

    internal async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        Stop();
        while (Volatile.Read(ref _evaluationActive) != 0
            || Volatile.Read(ref _runtimeEventDrainActive) != 0
            || Volatile.Read(ref _periodicEvaluationActive) != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
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
            return _tasks.Select(CloneTask).ToList();
        }
    }

    public void SaveTasks(IReadOnlyList<TriggerTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_syncLock)
        {
            _tasks = tasks.Select(NormalizeAndCloneTask).ToList();
            Save();
        }

        _appendLog("Info", TriggerLog, GetString("Triggers.Log.Saved"), $"{tasks.Count} task(s)");
    }

    public void AddTask(TriggerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_syncLock)
        {
            _tasks.Add(NormalizeAndCloneTask(task));
            Save();
        }

        _appendLog("Info", TriggerLog, string.Format(CultureInfo.CurrentCulture, GetString("Triggers.Log.Added.Format"), task.Name), task.Id);
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
            _appendLog("Info", TriggerLog, string.Format(CultureInfo.CurrentCulture, GetString("Triggers.Log.Deleted.Format"), deletedName), id);
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
            _appendLog("Info", TriggerLog, string.Format(CultureInfo.CurrentCulture, GetString("Triggers.Log.Moved.Format"), movedName), direction.ToString(CultureInfo.InvariantCulture));
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
        if (!TryBeginEvaluation())
        {
            return [];
        }

        try
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
                results.Add(new TriggerExecutionResult(task.Id, task.Name, triggeredAt, task.Actions.ToArray()));
                _appendLog(
                    "Info",
                    TriggerLog,
                    string.Format(CultureInfo.CurrentCulture, _getString("Triggers.Log.Fired.Format"), task.Name),
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
                PersistTriggeredAt(results);
            }

            return results;
        }
        finally
        {
            Interlocked.Decrement(ref _evaluationActive);
        }
    }

    private bool TryBeginEvaluation()
    {
        lock (_syncLock)
        {
            if (Volatile.Read(ref _acceptRuntimeWork) == 0)
            {
                return false;
            }

            Interlocked.Increment(ref _evaluationActive);
            return true;
        }
    }

    private void PersistTriggeredAt(IReadOnlyList<TriggerExecutionResult> results)
    {
        Dictionary<string, DateTimeOffset> triggeredAtByTaskId = results.ToDictionary(
            static result => result.TaskId,
            static result => result.TriggeredAt,
            StringComparer.Ordinal);
        lock (_syncLock)
        {
            foreach (TriggerTask task in _tasks)
            {
                if (triggeredAtByTaskId.TryGetValue(task.Id, out DateTimeOffset triggeredAt))
                {
                    task.LastTriggeredAt = triggeredAt;
                }
            }

            Save();
        }
    }

    internal static TriggerService CreateDefault(IApplicationActionDispatcher actions)
    {
        return new TriggerService(
            Path.Combine(AppDataPathService.ResolveLocalDataDirectory(), "Triggers.json"),
            actions,
            NotificationService.Instance,
            TriggerRuntimeEventHub.Instance,
            LogStorageService.Instance.AppendLog,
            LocalizationService.Instance.GetString,
            triggerEvent => TriggerEvaluationContextFactory.Create(triggerEvent.EventKind, triggerEvent.NotificationLevel),
            () => AppSettingsService.Instance.TriggersEnabled,
            value => AppSettingsService.Instance.TriggersEnabled = value,
            () => AppSettingsService.Instance.TriggerNotificationsEnabled);
    }

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
            TriggerActionKind.SetLaunchAtStartup => ApplicationActionKind.SetLaunchAtStartup,
            TriggerActionKind.SetTransparentProxy => ApplicationActionKind.SetTransparentProxy,
            TriggerActionKind.SetConnectionSampling => ApplicationActionKind.SetConnectionSampling,
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
        bool startDrain;
        lock (_syncLock)
        {
            if (Volatile.Read(ref _acceptRuntimeWork) == 0
                || (triggerEvent.EventKind == TriggerEventKind.NotificationRaised
                    && Volatile.Read(ref _triggerGeneratedNotificationSuppressionDepth) > 0))
            {
                return;
            }

            _pendingRuntimeEvents.Enqueue(triggerEvent);
            startDrain = _runtimeEventDrainActive == 0;
            if (startDrain)
            {
                _runtimeEventDrainActive = 1;
            }
        }

        if (!startDrain)
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
            bool restartDrain;
            lock (_syncLock)
            {
                _runtimeEventDrainActive = 0;
                restartDrain = Volatile.Read(ref _acceptRuntimeWork) == 1
                    && !_pendingRuntimeEvents.IsEmpty;
                if (restartDrain)
                {
                    _runtimeEventDrainActive = 1;
                }
            }

            if (restartDrain)
            {
                _ = DrainRuntimeEventsAsync();
            }
        }
    }

    private void OnPeriodicTimer(object? state)
    {
        lock (_syncLock)
        {
            if (Volatile.Read(ref _acceptRuntimeWork) == 0
                || !TriggersEnabled
                || _periodicEvaluationActive == 1)
            {
                return;
            }

            _periodicEvaluationActive = 1;
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
            string.Format(CultureInfo.CurrentCulture, GetString("Triggers.Log.ActionFailed.Format"), task.Name),
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
            TriggerConditionKind.UploadRate => context.UploadBytesPerSecond >= condition.Threshold,
            TriggerConditionKind.DownloadRate => context.DownloadBytesPerSecond >= condition.Threshold,
            TriggerConditionKind.ActiveConnections => context.ActiveConnectionCount >= condition.Threshold,
            TriggerConditionKind.SessionTraffic => context.SessionTrafficBytes >= condition.Threshold,
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
            if (json.TrimStart().StartsWith('['))
            {
                _tasks = (JsonSerializer.Deserialize<List<TriggerTask>>(json) ?? []).Select(CloneTask).ToList();
                return;
            }

            TriggerStoreDocument? document = JsonSerializer.Deserialize<TriggerStoreDocument>(json);
            _tasks = document?.Tasks is null ? [] : document.Tasks.Select(CloneTask).ToList();
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
        File.WriteAllText(_storagePath, JsonSerializer.Serialize(document, JsonOptions));
    }

    private sealed record TriggerStoreDocument(IReadOnlyList<TriggerTask> Tasks);

    private static TriggerTask CloneTask(TriggerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new TriggerTask(
            task.Id,
            task.Name,
            task.IsEnabled,
            task.Conditions.ToArray(),
            task.Actions.ToArray(),
            task.LastTriggeredAt);
    }

    private static TriggerTask NormalizeAndCloneTask(TriggerTask task)
    {
        return CloneTask(TriggerTaskNormalizer.Normalize(task).Task);
    }
}
