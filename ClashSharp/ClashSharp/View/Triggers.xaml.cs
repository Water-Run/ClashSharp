/*
 * Triggers Page
 * Hosts ordered user trigger tasks and searchable add-task dialogs
 *
 * @author: WaterRun
 * @file: View/Triggers.xaml.cs
 * @date: 2026-06-26
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TriggerTaskAction = ClashSharp.Model.TriggerAction;

namespace ClashSharp.View;

/// <summary>Page for managing ordered trigger tasks.</summary>
public sealed partial class Triggers : Page
{
    private readonly TriggersViewModel _viewModel;
    private TriggerTaskItemViewModel? _editingItem;

    public Triggers()
    {
        _viewModel = new(LocalizationService.Instance.GetString, TriggerService.Instance);
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void AddTriggerCardButton_Click(object sender, RoutedEventArgs e)
    {
        ShowTriggerEditorForNewTask();
    }

    private void EditTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TriggerTaskItemViewModel item })
        {
            ShowTriggerEditor(item);
        }
    }

    private void OpenTriggerLogsButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(Logs), "Trigger");
    }

    private void EnableAllTriggersButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetAllTasksEnabled(true);
    }

    private void DisableAllTriggersButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetAllTasksEnabled(false);
    }

    private void MoveTriggerUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            _viewModel.MoveTask(id, -1);
        }
    }

    private void MoveTriggerDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            _viewModel.MoveTask(id, 1);
        }
    }

    private async void DeleteTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Triggers.Delete.Title"),
            Content = LocalizationService.Instance.GetString("Triggers.Delete.Message"),
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Delete"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            _viewModel.DeleteTask(id);
        }
    }

    private void TriggerNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: TriggerTaskItemViewModel item })
        {
            _viewModel.UpdateTask(item);
        }
    }

    private void TriggerEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: TriggerTaskItemViewModel item })
        {
            _viewModel.UpdateTask(item);
        }
    }

    private void ShowTriggerEditorForNewTask()
    {
        ShowTriggerEditor(null);
    }

    private void ShowTriggerEditor(TriggerTaskItemViewModel? item)
    {
        _editingItem = item;
        TriggerEditorTitleText.Text = item is null
            ? LocalizationService.Instance.GetString("Triggers.Add")
            : item.Name;
        TriggerEditorNameBox.Header = LocalizationService.Instance.GetString("Triggers.Name");
        TriggerEditorNameBox.Text = item?.Name ?? LocalizationService.Instance.GetString("Triggers.DefaultName");

        TriggerConditionKind selectedCondition = item?.Task.Conditions.FirstOrDefault()?.Kind ?? TriggerConditionKind.AppEntered;
        HashSet<TriggerActionKind> selectedActions = item is null
            ? [TriggerActionKind.SendNotification]
            : item.Task.Actions.Select(static action => action.Kind).ToHashSet();

        TriggerConditionList.SearchPlaceholder = LocalizationService.Instance.GetString("Triggers.SearchConditions");
        TriggerConditionList.AllowMultiple = false;
        TriggerConditionList.SetOptions(BuildConditionOptions(selectedCondition));
        TriggerActionList.SearchPlaceholder = LocalizationService.Instance.GetString("Triggers.SearchActions");
        TriggerActionList.AllowMultiple = true;
        TriggerActionList.SetOptions(BuildActionOptions(selectedActions));

        TriggerListHost.Visibility = Visibility.Collapsed;
        TriggerEditorHost.Visibility = Visibility.Visible;
    }

    private void OpenTriggerList()
    {
        _editingItem = null;
        TriggerEditorHost.Visibility = Visibility.Collapsed;
        TriggerListHost.Visibility = Visibility.Visible;
    }

    private void CancelTriggerEditButton_Click(object sender, RoutedEventArgs e)
    {
        OpenTriggerList();
    }

    private void SaveTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<TriggerCondition> conditions = TriggerConditionList.SelectedOptions
            .Select(static option => option.Payload)
            .OfType<TriggerCondition>()
            .Take(1)
            .ToList();
        IReadOnlyList<TriggerTaskAction> actions = TriggerActionList.SelectedOptions
            .Select(static option => option.Payload)
            .OfType<TriggerTaskAction>()
            .ToList();

        conditions = conditions.Count == 0 ? [new TriggerCondition(TriggerConditionKind.AppEntered)] : conditions;
        actions = actions.Count == 0
            ? [new TriggerTaskAction(TriggerActionKind.SendNotification, LocalizationService.Instance.GetString("Notification.Custom.Message"))]
            : actions;

        if (_editingItem is TriggerTaskItemViewModel item)
        {
            item.Name = TriggerEditorNameBox.Text;
            item.Task.Conditions = conditions;
            item.Task.Actions = actions;
            _viewModel.UpdateTask(item);
        }
        else
        {
            _viewModel.AddTask(new TriggerTask(
                Guid.NewGuid().ToString("N"),
                ValidateTriggerName(TriggerEditorNameBox.Text),
                isEnabled: true,
                conditions,
                actions));
        }

        OpenTriggerList();
    }

    private string ValidateTriggerName(string name)
    {
        return _viewModel.ValidateTriggerName(name);
    }

    private static TextBlock BuildSectionLabel(string key)
    {
        return new TextBlock
        {
            Text = LocalizationService.Instance.GetString(key),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
    }

    private static IReadOnlyList<SearchableOptionItem> BuildConditionOptions(TriggerConditionKind selectedKind)
    {
        return
        [
            CreateConditionOption(TriggerConditionKind.AppEntered, new TriggerCondition(TriggerConditionKind.AppEntered), "\uE7C1", selectedKind == TriggerConditionKind.AppEntered),
            CreateConditionOption(TriggerConditionKind.ProxyStarted, new TriggerCondition(TriggerConditionKind.ProxyStarted), "\uE968", selectedKind == TriggerConditionKind.ProxyStarted),
            CreateConditionOption(TriggerConditionKind.NotificationRaised, new TriggerCondition(TriggerConditionKind.NotificationRaised, 0, NotificationLevel.CriticalOnly.ToString()), "\uEA8F", selectedKind == TriggerConditionKind.NotificationRaised),
            CreateConditionOption(TriggerConditionKind.TotalTraffic, new TriggerCondition(TriggerConditionKind.TotalTraffic, 1024L * 1024L * 1024L), "\uE9D2", selectedKind == TriggerConditionKind.TotalTraffic),
            CreateConditionOption(TriggerConditionKind.TrafficInWindow, new TriggerCondition(TriggerConditionKind.TrafficInWindow, 100L * 1024L * 1024L), "\uE81C", selectedKind == TriggerConditionKind.TrafficInWindow),
            CreateConditionOption(TriggerConditionKind.Runtime, new TriggerCondition(TriggerConditionKind.Runtime, 3600), "\uE823", selectedKind == TriggerConditionKind.Runtime),
            CreateConditionOption(TriggerConditionKind.SystemTime, new TriggerCondition(TriggerConditionKind.SystemTime, 0, "23:00"), "\uE121", selectedKind == TriggerConditionKind.SystemTime),
        ];
    }

    private static IReadOnlyList<SearchableOptionItem> BuildActionOptions(IReadOnlySet<TriggerActionKind> selectedKinds)
    {
        return
        [
            CreateActionOption(TriggerActionKind.CloseConnections, new TriggerTaskAction(TriggerActionKind.CloseConnections), "\uE711", selectedKinds.Contains(TriggerActionKind.CloseConnections)),
            CreateActionOption(TriggerActionKind.SetTransparentProxy, new TriggerTaskAction(TriggerActionKind.SetTransparentProxy, bool.TrueString), "\uE8A7", selectedKinds.Contains(TriggerActionKind.SetTransparentProxy)),
            CreateActionOption(TriggerActionKind.SwitchProxyMode, new TriggerTaskAction(TriggerActionKind.SwitchProxyMode, ClashSharpMode.RuleTakeover.ToString()), "\uE8AB", selectedKinds.Contains(TriggerActionKind.SwitchProxyMode)),
            CreateActionOption(TriggerActionKind.ExitApplication, new TriggerTaskAction(TriggerActionKind.ExitApplication), "\uE8BB", selectedKinds.Contains(TriggerActionKind.ExitApplication)),
            CreateActionOption(TriggerActionKind.SendNotification, new TriggerTaskAction(TriggerActionKind.SendNotification, "Trigger fired"), "\uEA8F", selectedKinds.Contains(TriggerActionKind.SendNotification)),
        ];
    }

    private static SearchableOptionItem CreateConditionOption(TriggerConditionKind kind, TriggerCondition condition, string glyph, bool isChecked = false)
    {
        return new SearchableOptionItem(
            kind.ToString(),
            LocalizationService.Instance.GetString($"Triggers.Condition.{kind}"),
            LocalizationService.Instance.GetString("Triggers.Conditions"),
            LocalizationService.Instance.GetString($"Triggers.Condition.{kind}.Description"),
            glyph,
            condition,
            isChecked);
    }

    private static SearchableOptionItem CreateActionOption(TriggerActionKind kind, TriggerTaskAction action, string glyph, bool isChecked = false)
    {
        return new SearchableOptionItem(
            kind.ToString(),
            LocalizationService.Instance.GetString($"Triggers.Action.{kind}"),
            LocalizationService.Instance.GetString("Triggers.Actions"),
            LocalizationService.Instance.GetString($"Triggers.Action.{kind}.Description"),
            glyph,
            action,
            isChecked);
    }
}
