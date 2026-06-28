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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Bindable trigger task panel view model.</summary>
internal sealed class TriggersViewModel : ObservableObject
{
    internal const int MaxTriggerNameLength = 48;

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

    public string AddDescriptionText => _getString("Triggers.Add.Description");

    public string ConditionDescriptionText => _getString("Triggers.Condition.Description");

    public string ActionDescriptionText => _getString("Triggers.Action.Description");

    public string ConditionSearchPlaceholderText => _getString("Triggers.Condition.SearchPlaceholder");

    public string ActionSearchPlaceholderText => _getString("Triggers.Action.SearchPlaceholder");

    public string NameText => _getString("Triggers.Name");

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
                OnPropertyChanged(nameof(CanEditTriggers));
                OnPropertyChanged(nameof(IsDisabledNoticeVisible));
            }
        }
    }

    public bool CanEditTriggers => TriggersEnabled;

    public bool IsDisabledNoticeVisible => !TriggersEnabled;

    public bool IsEmpty => TriggerTasks.Count == 0;

    public void Reload()
    {
        TriggerTasks.Clear();
        foreach (TriggerTask task in _triggerService.GetTasks())
        {
            TriggerTasks.Add(new TriggerTaskItemViewModel(task, _getString));
        }

        SetProperty(ref _triggersEnabled, _triggerService.TriggersEnabled, nameof(TriggersEnabled));
        OnPropertyChanged(nameof(CanEditTriggers));
        OnPropertyChanged(nameof(IsDisabledNoticeVisible));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void AddTask(TriggerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Name = ValidateTriggerName(task.Name, task.Id);
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
        item.Name = ValidateTriggerName(item.Name, item.Id);
        _triggerService.SaveTasks(TriggerTasks.Select(static row => row.Task).ToList());
        Reload();
    }

    public void SetAllTasksEnabled(bool isEnabled)
    {
        _triggerService.SetAllTasksEnabled(isEnabled);
        Reload();
    }

    public string ValidateTriggerName(string name, string? currentId = null)
    {
        string normalized = string.IsNullOrWhiteSpace(name) ? _getString("Triggers.DefaultName") : name.Trim();
        if (normalized.Length > MaxTriggerNameLength)
        {
            normalized = normalized[..MaxTriggerNameLength];
        }

        HashSet<string> existingNames = TriggerTasks
            .Where(item => !StringComparer.Ordinal.Equals(item.Id, currentId))
            .Select(static item => item.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!existingNames.Contains(normalized))
        {
            return normalized;
        }

        string baseName = normalized;
        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (candidate.Length > MaxTriggerNameLength)
            {
                candidate = $"{baseName[..Math.Max(0, MaxTriggerNameLength - suffix.ToString(CultureInfo.InvariantCulture).Length - 1)]} {suffix}";
            }

            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{Guid.NewGuid():N}"[..MaxTriggerNameLength];
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
            if (normalized.Length > TriggersViewModel.MaxTriggerNameLength)
            {
                normalized = normalized[..TriggersViewModel.MaxTriggerNameLength];
            }
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
        string title = _getString($"Triggers.Condition.{condition.Kind}");
        return condition.Kind switch
        {
            TriggerConditionKind.TotalTraffic or TriggerConditionKind.TrafficInWindow =>
                $"{title} >= {FormatBytes(condition.Threshold)}{FormatTrafficScope(condition.Value)}",
            TriggerConditionKind.Runtime =>
                $"{title} >= {FormatDuration(condition.Threshold)}",
            TriggerConditionKind.SystemTime when !string.IsNullOrWhiteSpace(condition.Value) =>
                $"{title} >= {condition.Value}",
            TriggerConditionKind.NotificationRaised when !string.IsNullOrWhiteSpace(condition.Value) =>
                $"{title}: {condition.Value}",
            _ => title,
        };
    }

    private string FormatAction(TriggerAction action)
    {
        return _getString($"Triggers.Action.{action.Kind}");
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

    private static string FormatDuration(long seconds)
    {
        if (seconds >= 3600 && seconds % 3600 == 0)
        {
            return $"{seconds / 3600:N0} h";
        }

        if (seconds >= 60 && seconds % 60 == 0)
        {
            return $"{seconds / 60:N0} min";
        }

        return $"{seconds:N0} s";
    }

    private static string FormatTrafficScope(string value)
    {
        return value switch
        {
            "Scheduled" => " · 定时",
            "Startup" => " · 自启动",
            "Cumulative" => " · 累计",
            _ => string.Empty,
        };
    }
}
