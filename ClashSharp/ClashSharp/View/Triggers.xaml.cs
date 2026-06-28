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
using System.Collections.ObjectModel;
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
    private readonly ObservableCollection<SearchableOptionItem> _selectedActionRows = [];
    private TriggerTaskItemViewModel? _editingItem;
    private TriggerCondition _selectedCondition = new(TriggerConditionKind.AppEntered);
    private IReadOnlyList<TriggerTaskAction> _selectedActions = [];
    private bool _isUpdatingConditionParameters;

    public Triggers()
    {
        _viewModel = new(LocalizationService.Instance.GetString, TriggerService.Instance);
        InitializeComponent();
        SelectedTriggerActionsList.ItemsSource = _selectedActionRows;
        DataContext = _viewModel;
        ShowTriggerEditorStoryboard.Completed += (_, _) =>
        {
            TriggerListHost.Visibility = Visibility.Collapsed;
            TriggerListHost.Opacity = 1;
            TriggerListHostTranslateTransform.X = 0;
            TriggerEditorHost.Opacity = 1;
            TriggerEditorHostTranslateTransform.X = 0;
        };
        ShowTriggerListStoryboard.Completed += (_, _) =>
        {
            TriggerEditorHost.Visibility = Visibility.Collapsed;
            TriggerEditorHost.Opacity = 0;
            TriggerEditorHostTranslateTransform.X = 0;
            TriggerListHost.Opacity = 1;
            TriggerListHostTranslateTransform.X = 0;
        };
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

        ThemedContentDialog dialog = new()
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

        SetSelectedCondition(item?.Task.Conditions.FirstOrDefault() ?? new TriggerCondition(TriggerConditionKind.AppEntered));
        SetSelectedActions(item is null || item.Task.Actions.Count == 0
            ? CreateDefaultActions()
            : item.Task.Actions);

        BeginShowTriggerEditorTransition();
    }

    private void OpenTriggerList()
    {
        _editingItem = null;
        BeginShowTriggerListTransition();
    }

    private void BeginShowTriggerEditorTransition()
    {
        ShowTriggerListStoryboard.Stop();
        ShowTriggerEditorStoryboard.Stop();
        TriggerListHost.Visibility = Visibility.Visible;
        TriggerEditorHost.Visibility = Visibility.Visible;
        TriggerListHost.Opacity = 1;
        TriggerEditorHost.Opacity = 0;
        TriggerListHostTranslateTransform.X = 0;
        TriggerEditorHostTranslateTransform.X = 32;
        ShowTriggerEditorStoryboard.Begin();
    }

    private void BeginShowTriggerListTransition()
    {
        ShowTriggerEditorStoryboard.Stop();
        ShowTriggerListStoryboard.Stop();
        TriggerListHost.Visibility = Visibility.Visible;
        TriggerEditorHost.Visibility = Visibility.Visible;
        TriggerListHost.Opacity = 0;
        TriggerEditorHost.Opacity = 1;
        TriggerListHostTranslateTransform.X = -24;
        TriggerEditorHostTranslateTransform.X = 0;
        ShowTriggerListStoryboard.Begin();
    }

    private void CancelTriggerEditButton_Click(object sender, RoutedEventArgs e)
    {
        OpenTriggerList();
    }

    private void BackToTriggerListButton_Click(object sender, RoutedEventArgs e)
    {
        OpenTriggerList();
    }

    private async void ChooseTriggerConditionButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowTriggerConditionPickerAsync();
    }

    private async void ChooseTriggerActionsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowTriggerActionPickerAsync();
    }

    private void SaveTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSelectedCondition(out TriggerCondition condition))
        {
            return;
        }

        IReadOnlyList<TriggerCondition> conditions = [condition];
        IReadOnlyList<TriggerTaskAction> actions = _selectedActions.Count == 0 ? CreateDefaultActions() : _selectedActions;

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

    private async System.Threading.Tasks.Task ShowTriggerConditionPickerAsync()
    {
        SearchableOptionList optionList = new()
        {
            SearchPlaceholder = LocalizationService.Instance.GetString("Triggers.SearchConditions"),
            AllowMultiple = false,
            MaxListHeight = 320,
        };
        optionList.SetOptions(BuildConditionOptions(_selectedCondition.Kind));

        ThemedContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Triggers.Conditions"),
            Content = optionList,
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Save"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        TriggerCondition? selectedCondition = optionList.SelectedOptions
            .Select(static option => option.Payload)
            .OfType<TriggerCondition>()
            .FirstOrDefault();
        if (selectedCondition is not null)
        {
            SetSelectedCondition(selectedCondition);
        }
    }

    private async System.Threading.Tasks.Task ShowTriggerActionPickerAsync()
    {
        HashSet<TriggerActionKind> selectedKinds = _selectedActions.Select(static action => action.Kind).ToHashSet();
        SearchableOptionList optionList = new()
        {
            SearchPlaceholder = LocalizationService.Instance.GetString("Triggers.SearchActions"),
            AllowMultiple = true,
            MaxListHeight = 380,
        };
        optionList.SetOptions(BuildActionOptions(selectedKinds));

        ThemedContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Triggers.Actions"),
            Content = optionList,
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Save"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        IReadOnlyList<TriggerTaskAction> selectedActions = optionList.SelectedOptions
            .Select(static option => option.Payload)
            .OfType<TriggerTaskAction>()
            .ToList();
        SetSelectedActions(selectedActions.Count == 0 ? CreateDefaultActions() : selectedActions);
    }

    private void SetSelectedCondition(TriggerCondition condition)
    {
        _selectedCondition = condition;
        SelectedTriggerConditionText.Text = LocalizationService.Instance.GetString($"Triggers.Condition.{condition.Kind}");
        SelectedTriggerConditionDescriptionText.Text = LocalizationService.Instance.GetString($"Triggers.Condition.{condition.Kind}.Description");
        ConfigureConditionParameterEditor(condition);
    }

    private void SetSelectedActions(IReadOnlyList<TriggerTaskAction> actions)
    {
        _selectedActions = actions.Count == 0 ? CreateDefaultActions() : actions.ToList();
        _selectedActionRows.Clear();
        foreach (TriggerTaskAction action in _selectedActions)
        {
            _selectedActionRows.Add(CreateActionOption(action.Kind, action, GetActionGlyph(action.Kind)));
        }
    }

    private static IReadOnlyList<TriggerTaskAction> CreateDefaultActions()
    {
        return [new TriggerTaskAction(TriggerActionKind.SendNotification, LocalizationService.Instance.GetString("Notification.Custom.Message"))];
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
            CreateActionOption(TriggerActionKind.CloseConnections, new TriggerTaskAction(TriggerActionKind.CloseConnections), GetActionGlyph(TriggerActionKind.CloseConnections), selectedKinds.Contains(TriggerActionKind.CloseConnections)),
            CreateActionOption(TriggerActionKind.SetTransparentProxy, new TriggerTaskAction(TriggerActionKind.SetTransparentProxy, bool.TrueString), GetActionGlyph(TriggerActionKind.SetTransparentProxy), selectedKinds.Contains(TriggerActionKind.SetTransparentProxy)),
            CreateActionOption(TriggerActionKind.SwitchProxyMode, new TriggerTaskAction(TriggerActionKind.SwitchProxyMode, ClashSharpMode.RuleTakeover.ToString()), GetActionGlyph(TriggerActionKind.SwitchProxyMode), selectedKinds.Contains(TriggerActionKind.SwitchProxyMode)),
            CreateActionOption(TriggerActionKind.ExitApplication, new TriggerTaskAction(TriggerActionKind.ExitApplication), GetActionGlyph(TriggerActionKind.ExitApplication), selectedKinds.Contains(TriggerActionKind.ExitApplication)),
            CreateActionOption(TriggerActionKind.SendNotification, new TriggerTaskAction(TriggerActionKind.SendNotification, "Trigger fired"), GetActionGlyph(TriggerActionKind.SendNotification), selectedKinds.Contains(TriggerActionKind.SendNotification)),
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

    private static string GetActionGlyph(TriggerActionKind kind)
    {
        return kind switch
        {
            TriggerActionKind.CloseConnections => "\uE711",
            TriggerActionKind.SetTransparentProxy => "\uE8A7",
            TriggerActionKind.SwitchProxyMode => "\uE8AB",
            TriggerActionKind.ExitApplication => "\uE8BB",
            _ => "\uEA8F",
        };
    }

    private void ConfigureConditionParameterEditor(TriggerCondition condition)
    {
        _isUpdatingConditionParameters = true;
        TriggerConditionValidationText.Visibility = Visibility.Collapsed;
        TriggerConditionThresholdBox.Visibility = Visibility.Collapsed;
        TriggerConditionUnitBox.Visibility = Visibility.Collapsed;
        TriggerConditionStartBox.Visibility = Visibility.Collapsed;
        TriggerConditionLevelBox.Visibility = Visibility.Collapsed;
        TriggerConditionValueBox.Visibility = Visibility.Collapsed;
        TriggerConditionUnitBox.Items.Clear();
        TriggerConditionStartBox.Items.Clear();
        TriggerConditionLevelBox.Items.Clear();

        switch (condition.Kind)
        {
            case TriggerConditionKind.TotalTraffic:
            case TriggerConditionKind.TrafficInWindow:
                TriggerConditionThresholdBox.Visibility = Visibility.Visible;
                TriggerConditionUnitBox.Visibility = Visibility.Visible;
                TriggerConditionStartBox.Visibility = Visibility.Visible;
                AddComboItems(TriggerConditionUnitBox, ["MB", "GB", "TB"]);
                AddComboItems(TriggerConditionStartBox, ["定时", "自启动", "累计"]);
                SetTrafficThreshold(condition.Threshold);
                TriggerConditionStartBox.SelectedIndex = condition.Value switch
                {
                    "Scheduled" => 0,
                    "Startup" => 1,
                    _ => 2,
                };
                break;
            case TriggerConditionKind.Runtime:
                TriggerConditionThresholdBox.Visibility = Visibility.Visible;
                TriggerConditionUnitBox.Visibility = Visibility.Visible;
                AddComboItems(TriggerConditionUnitBox, ["秒", "分钟", "小时"]);
                SetRuntimeThreshold(condition.Threshold);
                break;
            case TriggerConditionKind.SystemTime:
                TriggerConditionValueBox.Visibility = Visibility.Visible;
                TriggerConditionValueBox.Header = "时间";
                TriggerConditionValueBox.PlaceholderText = "23:00";
                TriggerConditionValueBox.Text = string.IsNullOrWhiteSpace(condition.Value) ? "23:00" : condition.Value;
                break;
            case TriggerConditionKind.NotificationRaised:
                TriggerConditionLevelBox.Visibility = Visibility.Visible;
                AddComboItems(TriggerConditionLevelBox, ["默认", "仅严重", "更多"]);
                TriggerConditionLevelBox.SelectedIndex = Enum.TryParse(condition.Value, out NotificationLevel level)
                    ? Math.Clamp((int)level, 0, 2)
                    : 0;
                break;
        }

        _isUpdatingConditionParameters = false;
    }

    private static void AddComboItems(ComboBox comboBox, IReadOnlyList<string> values)
    {
        foreach (string value in values)
        {
            comboBox.Items.Add(value);
        }

        comboBox.SelectedIndex = values.Count > 0 ? 0 : -1;
    }

    private void SetTrafficThreshold(long bytes)
    {
        double value = Math.Max(1, bytes);
        if (value >= 1024d * 1024d * 1024d * 1024d)
        {
            TriggerConditionThresholdBox.Value = value / 1024d / 1024d / 1024d / 1024d;
            TriggerConditionUnitBox.SelectedIndex = 2;
            return;
        }

        if (value >= 1024d * 1024d * 1024d)
        {
            TriggerConditionThresholdBox.Value = value / 1024d / 1024d / 1024d;
            TriggerConditionUnitBox.SelectedIndex = 1;
            return;
        }

        TriggerConditionThresholdBox.Value = value / 1024d / 1024d;
        TriggerConditionUnitBox.SelectedIndex = 0;
    }

    private void SetRuntimeThreshold(long seconds)
    {
        if (seconds >= 3600 && seconds % 3600 == 0)
        {
            TriggerConditionThresholdBox.Value = seconds / 3600d;
            TriggerConditionUnitBox.SelectedIndex = 2;
            return;
        }

        if (seconds >= 60 && seconds % 60 == 0)
        {
            TriggerConditionThresholdBox.Value = seconds / 60d;
            TriggerConditionUnitBox.SelectedIndex = 1;
            return;
        }

        TriggerConditionThresholdBox.Value = Math.Max(1, seconds);
        TriggerConditionUnitBox.SelectedIndex = 0;
    }

    private bool TryBuildSelectedCondition(out TriggerCondition condition)
    {
        condition = _selectedCondition;
        TriggerConditionValidationText.Visibility = Visibility.Collapsed;

        switch (_selectedCondition.Kind)
        {
            case TriggerConditionKind.TotalTraffic:
            case TriggerConditionKind.TrafficInWindow:
                if (!TryReadPositiveNumber(out double trafficValue))
                {
                    ShowConditionValidation("请输入大于 0 的流量阈值。");
                    return false;
                }

                long bytes = ConvertTrafficThresholdToBytes(trafficValue, TriggerConditionUnitBox.SelectedIndex);
                string start = TriggerConditionStartBox.SelectedIndex switch
                {
                    0 => "Scheduled",
                    1 => "Startup",
                    _ => "Cumulative",
                };
                condition = _selectedCondition with { Threshold = bytes, Value = start };
                break;
            case TriggerConditionKind.Runtime:
                if (!TryReadPositiveNumber(out double runtimeValue))
                {
                    ShowConditionValidation("请输入大于 0 的运行时长。");
                    return false;
                }

                condition = _selectedCondition with { Threshold = ConvertRuntimeThresholdToSeconds(runtimeValue, TriggerConditionUnitBox.SelectedIndex) };
                break;
            case TriggerConditionKind.SystemTime:
                if (!TimeOnly.TryParse(TriggerConditionValueBox.Text, out _))
                {
                    ShowConditionValidation("请输入 HH:mm 格式的时间。");
                    return false;
                }

                condition = _selectedCondition with { Value = TriggerConditionValueBox.Text.Trim() };
                break;
            case TriggerConditionKind.NotificationRaised:
                NotificationLevel level = (NotificationLevel)Math.Clamp(TriggerConditionLevelBox.SelectedIndex, 0, 2);
                condition = _selectedCondition with { Value = level.ToString() };
                break;
        }

        _selectedCondition = condition;
        SetSelectedCondition(condition);
        return true;
    }

    private bool TryReadPositiveNumber(out double value)
    {
        value = TriggerConditionThresholdBox.Value;
        return !double.IsNaN(value) && value > 0;
    }

    private static long ConvertTrafficThresholdToBytes(double value, int unitIndex)
    {
        double multiplier = unitIndex switch
        {
            1 => 1024d * 1024d * 1024d,
            2 => 1024d * 1024d * 1024d * 1024d,
            _ => 1024d * 1024d,
        };
        return Math.Max(1, (long)Math.Round(value * multiplier));
    }

    private static long ConvertRuntimeThresholdToSeconds(double value, int unitIndex)
    {
        double multiplier = unitIndex switch
        {
            1 => 60d,
            2 => 3600d,
            _ => 1d,
        };
        return Math.Max(1, (long)Math.Round(value * multiplier));
    }

    private void ShowConditionValidation(string message)
    {
        TriggerConditionValidationText.Text = message;
        TriggerConditionValidationText.Visibility = Visibility.Visible;
    }

    private void TriggerConditionThresholdBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        HideConditionValidationAfterUserEdit();
    }

    private void TriggerConditionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HideConditionValidationAfterUserEdit();
    }

    private void TriggerConditionValueBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        HideConditionValidationAfterUserEdit();
    }

    private void HideConditionValidationAfterUserEdit()
    {
        if (!_isUpdatingConditionParameters)
        {
            TriggerConditionValidationText.Visibility = Visibility.Collapsed;
        }
    }
}
