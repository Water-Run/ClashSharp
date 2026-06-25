/*
 * Triggers ViewModel
 * Owns trigger task list state, ordering, and localized page text
 *
 * @author: WaterRun
 * @file: ViewModel/TriggersViewModel.cs
 * @date: 2026-06-26
 */

#nullable enable

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Bindable trigger task panel view model.</summary>
internal sealed class TriggersViewModel : ObservableObject
{
    private readonly Func<string, string> _getString;
    private readonly TriggerService _triggerService;
    private bool _triggersEnabled;

    public TriggersViewModel(Func<string, string> getString, TriggerService triggerService)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _triggerService = triggerService ?? throw new ArgumentNullException(nameof(triggerService));
        MoveUpCommand = new RelayCommand(() => { });
        MoveDownCommand = new RelayCommand(() => { });
        Reload();
    }

    public string PageTitleText => _getString("Nav.Triggers");

    public string DescriptionText => _getString("Page.Triggers.Description");

    public string EnabledTitleText => _getString("Triggers.Enabled.Title");

    public string EnabledDescriptionText => _getString("Triggers.Enabled.Description");

    public string AddTriggerText => _getString("Triggers.Add");

    public string OpenTriggerLogsText => _getString("Triggers.OpenLogs");

    public string EmptyText => _getString("Triggers.Empty");

    public string ConditionsText => _getString("Triggers.Conditions");

    public string ActionsText => _getString("Triggers.Actions");

    public string LastTriggeredText => _getString("Triggers.LastTriggered");

    public ObservableCollection<TriggerTaskItemViewModel> TriggerTasks { get; } = [];

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public bool TriggersEnabled
    {
        get => _triggersEnabled;
        set
        {
            if (SetProperty(ref _triggersEnabled, value))
            {
                _triggerService.TriggersEnabled = value;
            }
        }
    }

    public bool IsEmpty => TriggerTasks.Count == 0;

    public void Reload()
    {
        TriggerTasks.Clear();
        foreach (TriggerTask task in _triggerService.GetTasks())
        {
            TriggerTasks.Add(new TriggerTaskItemViewModel(task, _getString));
        }

        SetProperty(ref _triggersEnabled, _triggerService.TriggersEnabled, nameof(TriggersEnabled));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void AddTask(TriggerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _triggerService.AddTask(task);
        Reload();
    }

    public void DeleteTask(string id)
    {
        _triggerService.DeleteTask(id);
        Reload();
    }

    public void MoveTask(string id, int direction)
    {
        _triggerService.MoveTask(id, direction);
        Reload();
    }

    public void UpdateTask(TriggerTaskItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _triggerService.SaveTasks(TriggerTasks.Select(static row => row.Task).ToList());
        Reload();
    }
}

/// <summary>Bindable row for one trigger task.</summary>
internal sealed class TriggerTaskItemViewModel : ObservableObject
{
    private readonly Func<string, string> _getString;
    private string _name;
    private bool _isEnabled;

    public TriggerTaskItemViewModel(TriggerTask task, Func<string, string> getString)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _name = task.Name;
        _isEnabled = task.IsEnabled;
    }

    public TriggerTask Task { get; }

    public string Id => Task.Id;

    public string Name
    {
        get => _name;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? _getString("Triggers.DefaultName") : value.Trim();
            if (SetProperty(ref _name, normalized))
            {
                Task.Name = normalized;
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
                Task.IsEnabled = value;
            }
        }
    }

    public string ConditionsSummary => string.Join(", ", Task.Conditions.Select(FormatCondition));

    public string ActionsSummary => string.Join(", ", Task.Actions.Select(FormatAction));

    public string ConditionsLabel => _getString("Triggers.Conditions");

    public string ActionsLabel => _getString("Triggers.Actions");

    public string LastTriggeredLabel => _getString("Triggers.LastTriggered");

    public string LastTriggeredSummary => Task.LastTriggeredAt is DateTimeOffset lastTriggered
        ? lastTriggered.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : _getString("Triggers.NeverTriggered");

    private string FormatCondition(TriggerCondition condition)
    {
        return _getString($"Triggers.Condition.{condition.Kind}");
    }

    private string FormatAction(TriggerAction action)
    {
        return _getString($"Triggers.Action.{action.Kind}");
    }
}
