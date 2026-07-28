using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model.Triggers;
using ClashSharpMode = global::ClashSharp.Model.ClashSharpMode;
using TriggerAction = global::ClashSharp.Model.Triggers.TriggerAction;
using TriggerActionKind = global::ClashSharp.Model.Triggers.TriggerActionKind;

namespace ClashSharp.ViewModel;

/// <summary>Owns a complete multi-condition, ordered-action trigger draft and asynchronous save state.</summary>
internal sealed class TriggerEditorViewModel : ObservableObject
{
    internal const int MaxNameLength = 48;

    private readonly Func<string, string> _getString;
    private readonly HashSet<string> _existingNames;
    private readonly Func<
        TriggerTaskDefinition,
        CancellationToken,
        Task<TriggerEditorSaveResult>> _saveAsync;
    private readonly IApplicationErrorSink _errorSink;
    private TriggerTaskDefinition? _original;
    private string _name;
    private bool _isEnabled;
    private TriggerConditionEditorViewModel? _selectedCondition;
    private TriggerActionEditorViewModel? _selectedAction;
    private string? _errorCode;
    private bool _isStale;
    private int _busy;

    /// <summary>Initializes a new or existing definition draft without mutating the source definition.</summary>
    public TriggerEditorViewModel(
        Func<string, string> getString,
        TriggerTaskDefinition? original,
        IEnumerable<string> existingNames,
        Func<TriggerTaskDefinition, CancellationToken, Task<TriggerEditorSaveResult>> saveAsync,
        IApplicationErrorSink errorSink,
        string? newId = null)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        ArgumentNullException.ThrowIfNull(existingNames);
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        _original = original;
        _existingNames = existingNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        Id = original?.Id ?? (string.IsNullOrWhiteSpace(newId) ? Guid.NewGuid().ToString("N") : newId);
        _name = original?.Name ?? _getString("Triggers.DefaultName");
        _isEnabled = original?.IsEnabled ?? true;

        if (original is null)
        {
            Conditions.Add(TriggerConditionEditorViewModel.Create(
                TriggerConditionTemplate.AppEntered,
                _getString));
            Actions.Add(TriggerActionEditorViewModel.Create(
                TriggerActionKind.SendNotification,
                _getString));
        }
        else
        {
            foreach (TriggerCondition condition in original.Conditions)
            {
                Conditions.Add(new TriggerConditionEditorViewModel(condition, _getString));
            }

            foreach (TriggerAction action in original.Actions)
            {
                Actions.Add(new TriggerActionEditorViewModel(action, _getString));
            }
        }

        _selectedCondition = Conditions.FirstOrDefault();
        _selectedAction = Actions.FirstOrDefault();
        ConditionOptions = CreateConditionOptions();
        ActionOptions = CreateActionOptions();
        TrafficScopeOptions = CreateTrafficScopeOptions();
        RateDirectionOptions = CreateRateDirectionOptions();
        NotificationLevelOptions = CreateNotificationLevelOptions();
        ProxyModeOptions = CreateProxyModeOptions();
    }

    public string Id { get; }

    public bool IsNew => _original is null;

    public string Title => IsNew ? _getString("Triggers.Add") : Name;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                ClearError();
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                ClearError();
            }
        }
    }

    public ObservableCollection<TriggerConditionEditorViewModel> Conditions { get; } = [];

    public ObservableCollection<TriggerActionEditorViewModel> Actions { get; } = [];

    public TriggerConditionEditorViewModel? SelectedCondition
    {
        get => _selectedCondition;
        set => SetProperty(ref _selectedCondition, value);
    }

    public TriggerActionEditorViewModel? SelectedAction
    {
        get => _selectedAction;
        set => SetProperty(ref _selectedAction, value);
    }

    public ReadOnlyCollection<TriggerEditorOption<TriggerConditionTemplate>> ConditionOptions { get; }

    public ReadOnlyCollection<TriggerEditorOption<TriggerActionKind>> ActionOptions { get; }

    public ReadOnlyCollection<TriggerEditorOption<TriggerTrafficScope>> TrafficScopeOptions { get; }

    public ReadOnlyCollection<TriggerEditorOption<TriggerTrafficDirection>> RateDirectionOptions { get; }

    public ReadOnlyCollection<TriggerEditorOption<TriggerNotificationLevel>> NotificationLevelOptions { get; }

    public ReadOnlyCollection<TriggerEditorOption<ClashSharpMode>> ProxyModeOptions { get; }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public bool IsStale => _isStale;

    public bool CanEdit => !IsBusy && !IsStale;

    public bool CanCancel => !IsBusy;

    public bool CanSave => CanEdit;

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

    public string? ErrorMessage => ErrorCode is null ? null : _getString(MapErrorResource(ErrorCode));

    public bool HasError => ErrorCode is not null;

    public TriggerConditionEditorViewModel AddCondition(TriggerConditionTemplate template)
    {
        TriggerConditionEditorViewModel condition = TriggerConditionEditorViewModel.Create(template, _getString);
        Conditions.Add(condition);
        SelectedCondition = condition;
        ClearError();
        return condition;
    }

    public bool RemoveCondition(TriggerConditionEditorViewModel condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        int index = Conditions.IndexOf(condition);
        if (index < 0)
        {
            return false;
        }

        Conditions.RemoveAt(index);
        SelectedCondition = Conditions.Count == 0
            ? null
            : Conditions[Math.Min(index, Conditions.Count - 1)];
        ClearError();
        return true;
    }

    public bool MoveCondition(TriggerConditionEditorViewModel condition, int direction)
    {
        ArgumentNullException.ThrowIfNull(condition);
        int index = Conditions.IndexOf(condition);
        int target = index + Math.Sign(direction);
        if (index < 0 || direction == 0 || target < 0 || target >= Conditions.Count)
        {
            return false;
        }

        Conditions.Move(index, target);
        SelectedCondition = condition;
        ClearError();
        return true;
    }

    public TriggerActionEditorViewModel AddAction(TriggerActionKind kind)
    {
        TriggerActionEditorViewModel action = TriggerActionEditorViewModel.Create(kind, _getString);
        int exitIndex = Actions.ToList().FindIndex(static item => item.Kind == TriggerActionKind.ExitApplication);
        if (kind != TriggerActionKind.ExitApplication && exitIndex >= 0)
        {
            Actions.Insert(exitIndex, action);
        }
        else
        {
            Actions.Add(action);
        }

        SelectedAction = action;
        ClearError();
        return action;
    }

    public bool RemoveAction(TriggerActionEditorViewModel action)
    {
        ArgumentNullException.ThrowIfNull(action);
        int index = Actions.IndexOf(action);
        if (index < 0)
        {
            return false;
        }

        Actions.RemoveAt(index);
        SelectedAction = Actions.Count == 0
            ? null
            : Actions[Math.Min(index, Actions.Count - 1)];
        ClearError();
        return true;
    }

    public bool MoveAction(TriggerActionEditorViewModel action, int direction)
    {
        ArgumentNullException.ThrowIfNull(action);
        int index = Actions.IndexOf(action);
        int target = index + Math.Sign(direction);
        if (index < 0 || direction == 0 || target < 0 || target >= Actions.Count)
        {
            return false;
        }

        if (action.Kind == TriggerActionKind.ExitApplication
            || Actions[target].Kind == TriggerActionKind.ExitApplication)
        {
            SetError("trigger.action.exit.must_be_final");
            return false;
        }

        Actions.Move(index, target);
        SelectedAction = action;
        ClearError();
        return true;
    }

    /// <summary>Builds one immutable definition after complete draft and domain validation.</summary>
    public bool TryBuildDefinition(out TriggerTaskDefinition? definition)
    {
        definition = null;
        ClearNestedErrors();
        string normalizedName = Name.Trim();
        if (normalizedName.Length == 0)
        {
            SetError("trigger.name.required");
            return false;
        }

        if (normalizedName.Length > MaxNameLength)
        {
            SetError("trigger.editor.name_too_long");
            return false;
        }

        if (_existingNames.Contains(normalizedName))
        {
            SetError("trigger.editor.name_duplicate");
            return false;
        }

        List<TriggerCondition> conditions = [];
        HashSet<string> conditionIds = new(StringComparer.Ordinal);
        foreach (TriggerConditionEditorViewModel conditionEditor in Conditions)
        {
            if (!conditionIds.Add(conditionEditor.Id))
            {
                SelectedCondition = conditionEditor;
                SetError("trigger.condition.id.duplicate");
                return false;
            }

            if (!conditionEditor.TryBuild(out TriggerCondition? condition) || condition is null)
            {
                SelectedCondition = conditionEditor;
                SetError(conditionEditor.ErrorCode ?? "trigger.condition.parameters.mismatch");
                return false;
            }

            conditions.Add(condition);
        }

        List<TriggerAction> actions = [];
        foreach (TriggerActionEditorViewModel actionEditor in Actions)
        {
            if (!actionEditor.TryBuild(out TriggerAction? action) || action is null)
            {
                SelectedAction = actionEditor;
                SetError(actionEditor.ErrorCode ?? "trigger.action.parameters.mismatch");
                return false;
            }

            actions.Add(action);
        }

        long baseRevision = _original?.Revision ?? 1;
        TriggerTaskDefinition candidate = new(
            Id,
            baseRevision,
            normalizedName,
            IsEnabled,
            conditions,
            actions);
        TriggerDefinitionValidationResult validation = TriggerDefinitionValidator.Validate(candidate);
        if (!validation.IsValid)
        {
            SetError(validation.Errors[0].Code);
            return false;
        }

        long revision = _original is null || DefinitionsEqual(candidate, _original)
            ? baseRevision
            : checked(baseRevision + 1);
        definition = revision == candidate.Revision
            ? candidate
            : new TriggerTaskDefinition(
                candidate.Id,
                revision,
                candidate.Name,
                candidate.IsEnabled,
                candidate.Conditions,
                candidate.Actions);
        Name = normalizedName;
        ClearError();
        return true;
    }

    /// <summary>Validates and asynchronously persists this complete draft exactly once at a time.</summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (IsStale)
        {
            SetError("trigger.definition.conflict");
            return false;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            SetError("trigger.editor.busy");
            return false;
        }

        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanSave));
        try
        {
            if (!TryBuildDefinition(out TriggerTaskDefinition? definition) || definition is null)
            {
                return false;
            }

            TriggerEditorSaveResult result;
            try
            {
                result = await _saveAsync(definition, cancellationToken);
            }
            catch (Exception exception) when (
                ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
            {
                throw;
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                await ReportUnexpectedAsync("Triggers.Editor.Save", exception);
                SetError("trigger.definition.write_unavailable");
                return false;
            }
            if (!result.IsSucceeded)
            {
                SetError(result.ErrorCode ?? "trigger.definition.write_unavailable");
                if (StringComparer.Ordinal.Equals(
                    result.ErrorCode,
                    "trigger.definition.conflict"))
                {
                    MarkStale();
                }

                return false;
            }

            _original = definition;
            ClearError();
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(Title));
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    private ReadOnlyCollection<TriggerEditorOption<TriggerConditionTemplate>> CreateConditionOptions()
    {
        return Array.AsReadOnly(Enum.GetValues<TriggerConditionTemplate>()
            .Select(template =>
            {
                TriggerConditionEditorViewModel draft = TriggerConditionEditorViewModel.Create(template, _getString);
                return new TriggerEditorOption<TriggerConditionTemplate>(
                    template,
                    draft.Title,
                    draft.Description);
            })
            .ToArray());
    }

    private ReadOnlyCollection<TriggerEditorOption<TriggerActionKind>> CreateActionOptions()
    {
        return Array.AsReadOnly(Enum.GetValues<TriggerActionKind>()
            .Select(kind => new TriggerEditorOption<TriggerActionKind>(
                kind,
                _getString($"Triggers.Action.{kind}"),
                _getString($"Triggers.Action.{kind}.Description")))
            .ToArray());
    }

    private ReadOnlyCollection<TriggerEditorOption<TriggerTrafficScope>> CreateTrafficScopeOptions()
    {
        return Array.AsReadOnly(new[]
        {
            new TriggerEditorOption<TriggerTrafficScope>(
                TriggerTrafficScope.RollingWindow,
                _getString("Triggers.Condition.Scope.RollingWindow"),
                string.Empty),
            new TriggerEditorOption<TriggerTrafficScope>(
                TriggerTrafficScope.CurrentSession,
                _getString("Triggers.Condition.Scope.CurrentSession"),
                string.Empty),
            new TriggerEditorOption<TriggerTrafficScope>(
                TriggerTrafficScope.AllTime,
                _getString("Triggers.Condition.Scope.AllTime"),
                string.Empty),
        });
    }

    private ReadOnlyCollection<TriggerEditorOption<TriggerTrafficDirection>> CreateRateDirectionOptions()
    {
        return Array.AsReadOnly(new[]
        {
            new TriggerEditorOption<TriggerTrafficDirection>(
                TriggerTrafficDirection.Upload,
                _getString("Triggers.Condition.UploadRate"),
                string.Empty),
            new TriggerEditorOption<TriggerTrafficDirection>(
                TriggerTrafficDirection.Download,
                _getString("Triggers.Condition.DownloadRate"),
                string.Empty),
        });
    }

    private ReadOnlyCollection<TriggerEditorOption<TriggerNotificationLevel>> CreateNotificationLevelOptions()
    {
        return Array.AsReadOnly(new[]
        {
            new TriggerEditorOption<TriggerNotificationLevel>(
                TriggerNotificationLevel.Default,
                _getString("Settings.Notification.Default"),
                string.Empty),
            new TriggerEditorOption<TriggerNotificationLevel>(
                TriggerNotificationLevel.CriticalOnly,
                _getString("Settings.Notification.CriticalOnly"),
                string.Empty),
            new TriggerEditorOption<TriggerNotificationLevel>(
                TriggerNotificationLevel.More,
                _getString("Settings.Notification.More"),
                string.Empty),
        });
    }

    private ReadOnlyCollection<TriggerEditorOption<ClashSharpMode>> CreateProxyModeOptions()
    {
        return Array.AsReadOnly(new[]
        {
            ClashSharpMode.Disabled,
            ClashSharpMode.Standby,
            ClashSharpMode.RuleTakeover,
            ClashSharpMode.FullTakeover,
        }.Select(mode => new TriggerEditorOption<ClashSharpMode>(
            mode,
            _getString($"Master.Mode.{mode}.Title"),
            string.Empty)).ToArray());
    }

    private static bool DefinitionsEqual(
        TriggerTaskDefinition left,
        TriggerTaskDefinition right)
    {
        return StringComparer.Ordinal.Equals(left.Id, right.Id)
            && StringComparer.Ordinal.Equals(left.Name, right.Name)
            && left.IsEnabled == right.IsEnabled
            && left.Conditions.SequenceEqual(right.Conditions)
            && left.Actions.SequenceEqual(right.Actions);
    }

    private void ClearNestedErrors()
    {
        ClearError();
    }

    private void ClearError()
    {
        ErrorCode = null;
    }

    private void SetError(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    private async Task ReportUnexpectedAsync(string operationName, Exception exception)
    {
        try
        {
            await _errorSink.ReportAsync(
                new ApplicationError(operationName, exception),
                CancellationToken.None);
        }
        catch (Exception sinkException) when (
            !ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
            // The primary operation remains represented by the typed presentation error.
        }
    }

    private void MarkStale()
    {
        if (_isStale)
        {
            return;
        }

        _isStale = true;
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
    }

    private static string MapErrorResource(string errorCode)
    {
        return errorCode switch
        {
            "trigger.name.required" => "Triggers.Validation.NameRequired",
            "trigger.editor.name_too_long" => "Triggers.Validation.NameTooLong",
            "trigger.editor.name_duplicate" => "Triggers.Validation.NameDuplicate",
            "trigger.editor.busy" => "Triggers.Validation.Busy",
            "trigger.definition.conflict" => "Triggers.Validation.Conflict",
            "trigger.conditions.required" => "Triggers.Validation.ConditionRequired",
            "trigger.actions.required" => "Triggers.Validation.ActionRequired",
            "trigger.action.exit.must_be_final" => "Triggers.Validation.ExitFinal",
            "trigger.action.notification.message.required" =>
                "Triggers.Validation.NotificationMessageRequired",
            "trigger.condition.time.invalid" => "Triggers.Validation.TimeFormat",
            "trigger.condition.threshold.invalid" => "Triggers.Validation.PositiveTraffic",
            "trigger.condition.window.invalid" => "Triggers.Validation.PositiveRuntime",
            _ => "Triggers.Validation.SaveFailed",
        };
    }
}
