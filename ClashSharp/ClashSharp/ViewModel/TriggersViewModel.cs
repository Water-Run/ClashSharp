#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ViewModel;

/// <summary>Presentation boundary for the global trigger enablement setting.</summary>
internal interface ITriggerPresentationSettings
{
    bool IsEnabled { get; set; }
}

/// <summary>Owns asynchronous trigger catalog CRUD, ordering, and the active editor draft.</summary>
internal sealed class TriggersViewModel : ObservableObject
{
    private readonly Func<string, string> _getString;
    private readonly ITriggerDefinitionStore _store;
    private readonly ITriggerPresentationSettings _settings;
    private long _generation;
    private bool _triggersEnabled;
    private TriggerEditorViewModel? _currentEditor;
    private string? _errorCode;
    private string? _errorResourceKey;
    private int _busy;

    public TriggersViewModel(
        Func<string, string> getString,
        ITriggerDefinitionStore store,
        ITriggerPresentationSettings settings)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _triggersEnabled = settings.IsEnabled;
    }

    public string PageTitleText => _getString("Nav.Triggers");

    public string DescriptionText => _getString("Page.Triggers.Description");

    public string AddTriggerText => _getString("Triggers.Add");

    public string AddDescriptionText => _getString("Triggers.Add.Description");

    public string ConditionDescriptionText => _getString("Triggers.Condition.Description");

    public string ActionDescriptionText => _getString("Triggers.Action.Description");

    public string NameText => _getString("Triggers.Name");

    public string EnabledText => _getString("Triggers.Enabled.Title");

    public string OpenTriggerLogsText => _getString("Triggers.OpenLogs");

    public string EnableAllText => _getString("Triggers.EnableAll");

    public string DisableAllText => _getString("Triggers.DisableAll");

    public string DisabledNoticeText => _getString("Triggers.DisabledNotice");

    public string EmptyText => _getString("Triggers.Empty");

    public string ConditionsText => _getString("Triggers.Conditions");

    public string ActionsText => _getString("Triggers.Actions");

    public string LastTriggeredText => _getString("Triggers.LastTriggered");

    public string SaveText => _getString("Command.Save");

    public string CancelText => _getString("Command.Cancel");

    public string AddText => _getString("Command.Add");

    public string DeleteText => _getString("Command.Delete");

    public string SearchConditionsText => _getString("Triggers.SearchConditions");

    public string SearchActionsText => _getString("Triggers.SearchActions");

    public string DeleteTitleText => _getString("Triggers.Delete.Title");

    public string DeleteMessageText => _getString("Triggers.Delete.Message");

    public string ConditionThresholdHeaderText => _getString("Triggers.Condition.Parameter.Threshold");

    public string ConditionScopeHeaderText => _getString("Triggers.Condition.Parameter.Scope");

    public string RateDirectionHeaderText => _getString("Triggers.Condition.Parameter.Direction");

    public string NotificationLevelHeaderText => _getString("Triggers.Condition.Parameter.NotificationLevel");

    public string ConditionTimeHeaderText => _getString("Triggers.Condition.Parameter.Time");

    public string WindowSecondsHeaderText => _getString("Triggers.Condition.Parameter.WindowSeconds");

    public string RuntimeSecondsHeaderText => _getString("Triggers.Condition.Parameter.RuntimeSeconds");

    public string BooleanValueHeaderText => _getString("Triggers.Action.Parameter.Enabled");

    public string ProxyModeHeaderText => _getString("Triggers.Action.Parameter.ProxyMode");

    public string NotificationMessageHeaderText => _getString("Triggers.Action.Parameter.Message");

    public ObservableCollection<TriggerTaskItemViewModel> TriggerTasks { get; } = [];

    public bool TriggersEnabled
    {
        get => _triggersEnabled;
        set
        {
            if (SetProperty(ref _triggersEnabled, value))
            {
                _settings.IsEnabled = value;
                OnPropertyChanged(nameof(CanEditTriggers));
                OnPropertyChanged(nameof(IsDisabledNoticeVisible));
            }
        }
    }

    public bool CanEditTriggers => TriggersEnabled && !IsBusy;

    public bool IsDisabledNoticeVisible => !TriggersEnabled;

    public bool IsEmpty => TriggerTasks.Count == 0;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public TriggerEditorViewModel? CurrentEditor
    {
        get => _currentEditor;
        private set
        {
            if (SetProperty(ref _currentEditor, value))
            {
                OnPropertyChanged(nameof(IsEditing));
                OnPropertyChanged(nameof(IsListing));
            }
        }
    }

    public bool IsEditing => CurrentEditor is not null;

    public bool IsListing => CurrentEditor is null;

    public string? ErrorCode
    {
        get => _errorCode;
        private set
        {
            if (SetProperty(ref _errorCode, value))
            {
                OnPropertyChanged(nameof(ErrorMessage));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string? ErrorMessage => ErrorCode is null
        ? null
        : _getString(_errorResourceKey ?? MapErrorResource(ErrorCode));

    public bool HasError => ErrorCode is not null;

    /// <summary>Loads the current repository generation without blocking the UI thread.</summary>
    public async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
        {
            return false;
        }

        try
        {
            TriggerPersistenceResult<TriggerDefinitionCatalog> result;
            try
            {
                result = await _store.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                SetError(
                    "trigger.definition.read_unavailable",
                    "Triggers.Validation.LoadFailed");
                return false;
            }
            if (!result.IsSucceeded || result.Value is not TriggerDefinitionCatalog catalog)
            {
                SetPersistenceError(
                    result.Status,
                    result.Diagnostic,
                    "trigger.definition.read_unavailable",
                    "Triggers.Validation.LoadFailed");
                return false;
            }

            ApplyCatalog(catalog);
            TriggersEnabled = _settings.IsEnabled;
            ClearError();
            return true;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Opens a new complete draft with safe typed defaults.</summary>
    public TriggerEditorViewModel BeginCreate()
    {
        long expectedGeneration = _generation;
        CurrentEditor = new TriggerEditorViewModel(
            _getString,
            original: null,
            TriggerTasks.Select(static task => task.Name),
            (definition, cancellationToken) =>
                SaveDefinitionAsync(expectedGeneration, definition, cancellationToken));
        return CurrentEditor;
    }

    /// <summary>Opens an existing immutable definition without dropping any condition or action.</summary>
    public TriggerEditorViewModel? BeginEdit(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        TriggerTaskItemViewModel? item = TriggerTasks.FirstOrDefault(
            task => StringComparer.Ordinal.Equals(task.Id, id));
        if (item is null)
        {
            SetError("trigger.definition.not_found");
            return null;
        }

        long expectedGeneration = _generation;
        CurrentEditor = new TriggerEditorViewModel(
            _getString,
            item.Definition,
            TriggerTasks
                .Where(task => !StringComparer.Ordinal.Equals(task.Id, id))
                .Select(static task => task.Name),
            (definition, cancellationToken) =>
                SaveDefinitionAsync(expectedGeneration, definition, cancellationToken));
        return CurrentEditor;
    }

    public void CancelEdit()
    {
        if (CurrentEditor?.IsBusy == true)
        {
            return;
        }

        CurrentEditor = null;
        ClearError();
    }

    public Task<bool> DeleteTaskAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!TriggerTasks.Any(task => StringComparer.Ordinal.Equals(task.Id, id)))
        {
            SetError("trigger.definition.not_found");
            return Task.FromResult(false);
        }

        return ReplaceDefinitionsAsync(
            TriggerTasks
                .Where(task => !StringComparer.Ordinal.Equals(task.Id, id))
                .Select(static task => task.Definition)
                .ToArray(),
            cancellationToken);
    }

    public Task<bool> MoveTaskAsync(string id, int direction, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        List<TriggerTaskDefinition> definitions = TriggerTasks
            .Select(static task => task.Definition)
            .ToList();
        int index = definitions.FindIndex(definition => StringComparer.Ordinal.Equals(definition.Id, id));
        int target = index + Math.Sign(direction);
        if (index < 0 || direction == 0 || target < 0 || target >= definitions.Count)
        {
            return Task.FromResult(false);
        }

        TriggerTaskDefinition moved = definitions[index];
        definitions.RemoveAt(index);
        definitions.Insert(target, moved);
        return ReplaceDefinitionsAsync(definitions, cancellationToken);
    }

    public Task<bool> SetAllTasksEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        if (TriggerTasks.All(task => task.Definition.IsEnabled == isEnabled))
        {
            ClearError();
            return Task.FromResult(true);
        }

        TriggerTaskDefinition[] definitions = TriggerTasks
            .Select(task => task.Definition.IsEnabled == isEnabled
                ? task.Definition
                : CopyDefinition(task.Definition, isEnabled: isEnabled))
            .ToArray();
        return ReplaceDefinitionsAsync(definitions, cancellationToken);
    }

    public async Task<bool> SetTaskEnabledAsync(
        string id,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        TriggerTaskItemViewModel? selected = TriggerTasks.FirstOrDefault(
            task => StringComparer.Ordinal.Equals(task.Id, id));
        if (selected is null)
        {
            SetError("trigger.definition.not_found");
            return false;
        }

        if (selected.Definition.IsEnabled == isEnabled)
        {
            ClearError();
            return true;
        }

        TriggerTaskDefinition[] definitions = TriggerTasks
            .Select(task => StringComparer.Ordinal.Equals(task.Id, id)
                && task.Definition.IsEnabled != isEnabled
                    ? CopyDefinition(task.Definition, isEnabled: isEnabled)
                    : task.Definition)
            .ToArray();
        bool replaced = await ReplaceDefinitionsAsync(definitions, cancellationToken);
        if (!replaced)
        {
            selected.RepublishEnabledState();
        }

        return replaced;
    }

    private async Task<TriggerEditorSaveResult> SaveDefinitionAsync(
        long expectedGeneration,
        TriggerTaskDefinition definition,
        CancellationToken cancellationToken)
    {
        List<TriggerTaskDefinition> definitions = TriggerTasks
            .Select(static task => task.Definition)
            .ToList();
        int index = definitions.FindIndex(existing =>
            StringComparer.Ordinal.Equals(existing.Id, definition.Id));
        if (index >= 0)
        {
            definitions[index] = definition;
        }
        else
        {
            definitions.Add(definition);
        }

        bool saved = await ReplaceDefinitionsAsync(
            definitions,
            cancellationToken,
            expectedGeneration);
        if (saved)
        {
            CurrentEditor = null;
            return TriggerEditorSaveResult.Succeeded();
        }

        return TriggerEditorSaveResult.Failed(
            ErrorCode ?? "trigger.definition.write_unavailable");
    }

    private async Task<bool> ReplaceDefinitionsAsync(
        IReadOnlyList<TriggerTaskDefinition> definitions,
        CancellationToken cancellationToken,
        long? expectedGeneration = null)
    {
        if (!TryBeginOperation())
        {
            return false;
        }

        try
        {
            TriggerPersistenceResult<TriggerDefinitionCatalog> result;
            try
            {
                result = await _store.ReplaceAsync(
                    expectedGeneration ?? _generation,
                    definitions,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                SetError("trigger.definition.write_unavailable");
                return false;
            }
            if (!result.IsSucceeded || result.Value is not TriggerDefinitionCatalog catalog)
            {
                SetPersistenceError(result.Status, result.Diagnostic, "trigger.definition.write_unavailable");
                if (result.Status == TriggerPersistenceStatus.Conflict)
                {
                    try
                    {
                        TriggerPersistenceResult<TriggerDefinitionCatalog> refreshed =
                            await _store.ReadAsync(cancellationToken);
                        if (refreshed.IsSucceeded
                            && refreshed.Value is TriggerDefinitionCatalog latest)
                        {
                            ApplyCatalog(latest);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // Refresh is best effort; retain the actionable optimistic-conflict result.
                    }
                }

                return false;
            }

            ApplyCatalog(catalog);
            ClearError();
            return true;
        }
        finally
        {
            EndOperation();
        }
    }

    private void ApplyCatalog(TriggerDefinitionCatalog catalog)
    {
        _generation = catalog.Generation;
        TriggerTasks.Clear();
        foreach (TriggerDefinitionCatalogItem task in catalog.Tasks)
        {
            TriggerTasks.Add(new TriggerTaskItemViewModel(
                task.Definition,
                task.LastTriggeredAt,
                _getString));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool TryBeginOperation()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            SetError("trigger.presentation.busy");
            return false;
        }

        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEditTriggers));
        return true;
    }

    private void EndOperation()
    {
        Interlocked.Exchange(ref _busy, 0);
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEditTriggers));
    }

    private void SetPersistenceError(
        TriggerPersistenceStatus status,
        TriggerDiagnostic? diagnostic,
        string fallbackCode,
        string? fallbackResourceKey = null)
    {
        switch (status)
        {
            case TriggerPersistenceStatus.Conflict:
                SetError("trigger.definition.conflict");
                break;
            case TriggerPersistenceStatus.NotFound:
                SetError("trigger.definition.not_found");
                break;
            default:
                SetError(diagnostic?.Code ?? fallbackCode, fallbackResourceKey);
                break;
        }
    }

    private void ClearError()
    {
        _errorResourceKey = null;
        ErrorCode = null;
    }

    private void SetError(string errorCode, string? resourceKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        bool resourceChanged = !StringComparer.Ordinal.Equals(
            _errorResourceKey,
            resourceKey);
        string? previousErrorCode = ErrorCode;
        _errorResourceKey = resourceKey;
        ErrorCode = errorCode;
        if (resourceChanged && StringComparer.Ordinal.Equals(previousErrorCode, errorCode))
        {
            OnPropertyChanged(nameof(ErrorMessage));
        }
    }

    private static TriggerTaskDefinition CopyDefinition(
        TriggerTaskDefinition definition,
        bool isEnabled)
    {
        return new TriggerTaskDefinition(
            definition.Id,
            checked(definition.Revision + 1),
            definition.Name,
            isEnabled,
            definition.Conditions,
            definition.Actions);
    }

    private static string MapErrorResource(string errorCode)
    {
        return errorCode switch
        {
            "trigger.presentation.busy" => "Triggers.Validation.Busy",
            "trigger.definition.conflict" => "Triggers.Validation.Conflict",
            "trigger.definition.not_found" => "Triggers.Validation.NotFound",
            "trigger.definition.read_unavailable" => "Triggers.Validation.LoadFailed",
            _ => "Triggers.Validation.SaveFailed",
        };
    }
}

/// <summary>Immutable bindable row for one persisted trigger definition.</summary>
internal sealed class TriggerTaskItemViewModel : ObservableObject
{
    private readonly Func<string, string> _getString;

    public TriggerTaskItemViewModel(
        TriggerTaskDefinition definition,
        DateTimeOffset? lastTriggeredAt,
        Func<string, string> getString)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        LastTriggeredAt = lastTriggeredAt;
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    public TriggerTaskDefinition Definition { get; }

    public DateTimeOffset? LastTriggeredAt { get; }

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public bool IsEnabled => Definition.IsEnabled;

    /// <summary>Re-publishes the durable value after a rejected one-way UI toggle.</summary>
    public void RepublishEnabledState() => OnPropertyChanged(nameof(IsEnabled));

    public string ConditionsSummary => string.Join(", ", Definition.Conditions.Select(FormatCondition));

    public string ActionsSummary => string.Join(", ", Definition.Actions.Select(FormatAction));

    public string ConditionsLabel => _getString("Triggers.Conditions");

    public string ActionsLabel => _getString("Triggers.Actions");

    public string LastTriggeredLabel => _getString("Triggers.LastTriggered");

    public string LastTriggeredSummary => LastTriggeredAt is DateTimeOffset lastTriggered
        ? lastTriggered.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : _getString("Triggers.NeverTriggered");

    private string FormatCondition(TriggerCondition condition)
    {
        return condition.Parameters switch
        {
            EventConditionParameters eventParameters =>
                _getString($"Triggers.Condition.{eventParameters.EventKind}"),
            NotificationConditionParameters notification =>
                $"{_getString("Triggers.Condition.NotificationRaised")}: {notification.MinimumLevel}",
            TrafficConditionParameters traffic => FormatTrafficCondition(traffic),
            RateConditionParameters rate =>
                $"{_getString(rate.Direction == TriggerTrafficDirection.Upload
                    ? "Triggers.Condition.UploadRate"
                    : "Triggers.Condition.DownloadRate")} >= {FormatBytes(rate.ThresholdBytesPerSecond)}/s",
            ActiveConnectionsConditionParameters connections =>
                $"{_getString("Triggers.Condition.ActiveConnections")} >= {connections.Threshold:N0}",
            RuntimeConditionParameters runtime =>
                $"{_getString("Triggers.Condition.Runtime")} >= {runtime.Threshold:g}",
            SystemTimeConditionParameters time =>
                $"{_getString("Triggers.Condition.SystemTime")} >= {time.TargetTime:t}",
            _ => _getString("Triggers.Validation.InvalidCondition"),
        };
    }

    private string FormatAction(TriggerAction action)
    {
        return _getString($"Triggers.Action.{action.Kind}");
    }

    private string TrafficTitle(TriggerTrafficScope scope)
    {
        return _getString(scope switch
        {
            TriggerTrafficScope.RollingWindow => "Triggers.Condition.TrafficInWindow",
            TriggerTrafficScope.CurrentSession => "Triggers.Condition.SessionTraffic",
            _ => "Triggers.Condition.TotalTraffic",
        });
    }

    private string FormatTrafficCondition(TrafficConditionParameters traffic)
    {
        string summary = $"{TrafficTitle(traffic.Scope)} >= {FormatBytes(traffic.ThresholdBytes)}";
        return traffic.Window is TimeSpan window
            ? $"{summary} · {window.ToString("g", CultureInfo.CurrentCulture)}"
            : summary;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N1} {units[unitIndex]}";
    }
}
