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

    public Triggers()
    {
        _viewModel = new(LocalizationService.Instance.GetString, TriggerService.Instance);
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void AddTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddTriggerDialogAsync();
    }

    private void OpenTriggerLogsButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(Logs), "Trigger");
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

    private async System.Threading.Tasks.Task ShowAddTriggerDialogAsync()
    {
        TextBox nameBox = new()
        {
            Header = LocalizationService.Instance.GetString("Triggers.Name"),
            Text = LocalizationService.Instance.GetString("Triggers.DefaultName"),
        };

        SearchableOptionList conditionList = new()
        {
            SearchPlaceholder = LocalizationService.Instance.GetString("Triggers.SearchConditions"),
            AllowMultiple = true,
            MaxListHeight = 240,
        };
        conditionList.SetOptions(BuildConditionOptions());

        SearchableOptionList actionList = new()
        {
            SearchPlaceholder = LocalizationService.Instance.GetString("Triggers.SearchActions"),
            AllowMultiple = true,
            MaxListHeight = 240,
        };
        actionList.SetOptions(BuildActionOptions());

        StackPanel content = new()
        {
            Spacing = 14,
            MinWidth = 520,
            MaxWidth = 720,
        };
        content.Children.Add(new TextBlock
        {
            Text = LocalizationService.Instance.GetString("Triggers.Add.Description"),
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        content.Children.Add(nameBox);
        content.Children.Add(BuildSectionLabel("Triggers.Conditions"));
        content.Children.Add(conditionList);
        content.Children.Add(BuildSectionLabel("Triggers.Actions"));
        content.Children.Add(actionList);

        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Triggers.Add"),
            Content = content,
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Add"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        IReadOnlyList<TriggerCondition> conditions = conditionList.SelectedOptions
            .Select(static option => option.Payload)
            .OfType<TriggerCondition>()
            .ToList();
        IReadOnlyList<TriggerTaskAction> actions = actionList.SelectedOptions
            .Select(static option => option.Payload)
            .OfType<TriggerTaskAction>()
            .ToList();

        _viewModel.AddTask(new TriggerTask(
            Guid.NewGuid().ToString("N"),
            nameBox.Text,
            isEnabled: true,
            conditions.Count == 0 ? [new TriggerCondition(TriggerConditionKind.AppEntered)] : conditions,
            actions.Count == 0 ? [new TriggerTaskAction(TriggerActionKind.SendNotification, "Trigger fired")] : actions));
    }

    private static TextBlock BuildSectionLabel(string key)
    {
        return new TextBlock
        {
            Text = LocalizationService.Instance.GetString(key),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
    }

    private static IReadOnlyList<SearchableOptionItem> BuildConditionOptions()
    {
        return
        [
            CreateConditionOption(TriggerConditionKind.AppEntered, new TriggerCondition(TriggerConditionKind.AppEntered), "\uE7C1", true),
            CreateConditionOption(TriggerConditionKind.ProxyStarted, new TriggerCondition(TriggerConditionKind.ProxyStarted), "\uE968"),
            CreateConditionOption(TriggerConditionKind.NotificationRaised, new TriggerCondition(TriggerConditionKind.NotificationRaised, 0, NotificationLevel.CriticalOnly.ToString()), "\uEA8F"),
            CreateConditionOption(TriggerConditionKind.TotalTraffic, new TriggerCondition(TriggerConditionKind.TotalTraffic, 1024L * 1024L * 1024L), "\uE9D2"),
            CreateConditionOption(TriggerConditionKind.TrafficInWindow, new TriggerCondition(TriggerConditionKind.TrafficInWindow, 100L * 1024L * 1024L), "\uE81C"),
            CreateConditionOption(TriggerConditionKind.Runtime, new TriggerCondition(TriggerConditionKind.Runtime, 3600), "\uE823"),
            CreateConditionOption(TriggerConditionKind.SystemTime, new TriggerCondition(TriggerConditionKind.SystemTime, 0, "23:00"), "\uE121"),
        ];
    }

    private static IReadOnlyList<SearchableOptionItem> BuildActionOptions()
    {
        return
        [
            CreateActionOption(TriggerActionKind.CloseConnections, new TriggerTaskAction(TriggerActionKind.CloseConnections), "\uE711"),
            CreateActionOption(TriggerActionKind.SetTransparentProxy, new TriggerTaskAction(TriggerActionKind.SetTransparentProxy, bool.TrueString), "\uE8A7"),
            CreateActionOption(TriggerActionKind.SwitchProxyMode, new TriggerTaskAction(TriggerActionKind.SwitchProxyMode, ClashSharpMode.RuleTakeover.ToString()), "\uE8AB"),
            CreateActionOption(TriggerActionKind.ExitApplication, new TriggerTaskAction(TriggerActionKind.ExitApplication), "\uE8BB"),
            CreateActionOption(TriggerActionKind.SendNotification, new TriggerTaskAction(TriggerActionKind.SendNotification, "Trigger fired"), "\uEA8F", true),
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
