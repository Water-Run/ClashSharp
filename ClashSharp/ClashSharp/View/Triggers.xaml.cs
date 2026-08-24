using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Components;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TriggerActionKind = global::ClashSharp.Model.Triggers.TriggerActionKind;

namespace ClashSharp.View;

/// <summary>Forwards WinUI interactions to the asynchronous, lossless trigger editor ViewModels.</summary>
public sealed partial class Triggers : Page
{
    private readonly TriggersViewModel _viewModel;
    private readonly IApplicationErrorSink _errorSink;
    private readonly Action _openLogs;
    private CancellationTokenSource? _pageLifetime;

    internal Triggers(TriggersPageDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _errorSink = dependencies.ErrorSink;
        _openLogs = dependencies.OpenLogs;
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_pageLifetime is not null)
        {
            return;
        }

        _pageLifetime = new CancellationTokenSource();
        await AwaitPageOperationAsync(token => _viewModel.LoadAsync(token));
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancellationTokenSource? lifetime = _pageLifetime;
        _pageLifetime = null;
        lifetime?.Cancel();
        lifetime?.Dispose();
    }

    private void AddTriggerCardButton_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.BeginCreate();
    }

    private void EditTriggerButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: TriggerTaskItemViewModel item })
        {
            _viewModel.BeginEdit(item.Id);
        }
    }

    private void OpenTriggerLogsButton_Click(object sender, RoutedEventArgs args)
    {
        _openLogs();
    }

    private async void EnableAllTriggersButton_Click(object sender, RoutedEventArgs args)
    {
        await AwaitPageOperationAsync(token => _viewModel.SetAllTasksEnabledAsync(true, token));
    }

    private async void DisableAllTriggersButton_Click(object sender, RoutedEventArgs args)
    {
        await AwaitPageOperationAsync(token => _viewModel.SetAllTasksEnabledAsync(false, token));
    }

    private async void MoveTriggerUpButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string id })
        {
            await AwaitPageOperationAsync(token => _viewModel.MoveTaskAsync(id, -1, token));
        }
    }

    private async void MoveTriggerDownButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string id })
        {
            await AwaitPageOperationAsync(token => _viewModel.MoveTaskAsync(id, 1, token));
        }
    }

    private async void DeleteTriggerButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        ThemedContentDialog dialog = new()
        {
            Title = _viewModel.DeleteTitleText,
            Content = _viewModel.DeleteMessageText,
            PrimaryButtonText = _viewModel.DeleteText,
            CloseButtonText = _viewModel.CancelText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowManagedAsync() is ContentDialogResult.Primary)
        {
            await AwaitPageOperationAsync(token => _viewModel.DeleteTaskAsync(id, token));
        }
    }

    private async void TriggerEnabledToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleSwitch { Tag: TriggerTaskItemViewModel item } toggle
            || toggle.IsOn == item.IsEnabled)
        {
            return;
        }

        await AwaitPageOperationAsync(token =>
            _viewModel.SetTaskEnabledAsync(item.Id, toggle.IsOn, token));
    }

    private void BackToTriggerListButton_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.CancelEdit();
    }

    private void CancelTriggerEditButton_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.CancelEdit();
    }

    private async void SaveTriggerButton_Click(object sender, RoutedEventArgs args)
    {
        TriggerEditorViewModel? editor = _viewModel.CurrentEditor;
        if (editor is not null)
        {
            await AwaitPageOperationAsync(token => editor.SaveAsync(token));
        }
    }

    private async void ChooseTriggerConditionButton_Click(object sender, RoutedEventArgs args)
    {
        TriggerEditorViewModel? editor = _viewModel.CurrentEditor;
        if (editor is null)
        {
            return;
        }

        SearchableOptionList options = new()
        {
            SearchPlaceholder = _viewModel.SearchConditionsText,
            AllowMultiple = false,
            MaxListHeight = 360,
        };
        options.SetOptions(editor.ConditionOptions.Select(option => new SearchableOptionItem(
            option.Value.ToString(),
            option.Title,
            _viewModel.ConditionsText,
            option.Description,
            GetConditionGlyph(option.Value),
            option.Value)));
        if (await ShowOptionDialogAsync(_viewModel.ConditionsText, options)
            && options.SelectedOptions.SingleOrDefault()?.Payload is TriggerConditionTemplate template)
        {
            editor.AddCondition(template);
        }
    }

    private async void ChooseTriggerActionButton_Click(object sender, RoutedEventArgs args)
    {
        TriggerEditorViewModel? editor = _viewModel.CurrentEditor;
        if (editor is null)
        {
            return;
        }

        SearchableOptionList options = new()
        {
            SearchPlaceholder = _viewModel.SearchActionsText,
            AllowMultiple = false,
            MaxListHeight = 360,
        };
        options.SetOptions(editor.ActionOptions.Select(option => new SearchableOptionItem(
            option.Value.ToString(),
            option.Title,
            _viewModel.ActionsText,
            option.Description,
            GetActionGlyph(option.Value),
            option.Value)));
        if (await ShowOptionDialogAsync(_viewModel.ActionsText, options)
            && options.SelectedOptions.SingleOrDefault()?.Payload is TriggerActionKind kind)
        {
            editor.AddAction(kind);
        }
    }

    private void MoveConditionUpButton_Click(object sender, RoutedEventArgs args) =>
        MoveCondition(sender, -1);

    private void MoveConditionDownButton_Click(object sender, RoutedEventArgs args) =>
        MoveCondition(sender, 1);

    private void MoveCondition(object sender, int direction)
    {
        if (sender is Button { Tag: TriggerConditionEditorViewModel condition })
        {
            _viewModel.CurrentEditor?.MoveCondition(condition, direction);
        }
    }

    private void RemoveConditionButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: TriggerConditionEditorViewModel condition })
        {
            _viewModel.CurrentEditor?.RemoveCondition(condition);
        }
    }

    private void MoveActionUpButton_Click(object sender, RoutedEventArgs args) =>
        MoveAction(sender, -1);

    private void MoveActionDownButton_Click(object sender, RoutedEventArgs args) =>
        MoveAction(sender, 1);

    private void MoveAction(object sender, int direction)
    {
        if (sender is Button { Tag: TriggerActionEditorViewModel action })
        {
            _viewModel.CurrentEditor?.MoveAction(action, direction);
        }
    }

    private void RemoveActionButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: TriggerActionEditorViewModel action })
        {
            _viewModel.CurrentEditor?.RemoveAction(action);
        }
    }

    private async Task<bool> ShowOptionDialogAsync(string title, SearchableOptionList content)
    {
        ThemedContentDialog dialog = new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = _viewModel.AddText,
            CloseButtonText = _viewModel.CancelText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowManagedAsync() is ContentDialogResult.Primary;
    }

    private async Task AwaitPageOperationAsync(Func<CancellationToken, Task> operation)
    {
        CancellationTokenSource? lifetime = _pageLifetime;
        if (lifetime is null)
        {
            return;
        }

        CancellationToken cancellationToken = lifetime.Token;
        try
        {
            await operation(cancellationToken);
        }
        catch (Exception exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportUnexpectedAsync("Triggers.PageOperation", exception);
        }
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
            // No secondary diagnostic channel is available at this presentation boundary.
        }
    }

    private static string GetConditionGlyph(TriggerConditionTemplate template)
    {
        return template switch
        {
            TriggerConditionTemplate.AppEntered => "\uE7C1",
            TriggerConditionTemplate.ProxyStarted => "\uE968",
            TriggerConditionTemplate.NotificationRaised => "\uEA8F",
            TriggerConditionTemplate.SystemTime => "\uE121",
            TriggerConditionTemplate.Runtime => "\uE823",
            TriggerConditionTemplate.ActiveConnections => "\uE839",
            _ => "\uE9D2",
        };
    }

    private static string GetActionGlyph(TriggerActionKind kind)
    {
        return kind switch
        {
            TriggerActionKind.CloseConnections => "\uE711",
            TriggerActionKind.SetLaunchAtStartup => "\uE7C3",
            TriggerActionKind.SetTransparentProxy => "\uE8A7",
            TriggerActionKind.SetConnectionSampling => "\uE81C",
            TriggerActionKind.SwitchProxyMode => "\uE8AB",
            TriggerActionKind.ExitApplication => "\uE8BB",
            _ => "\uEA8F",
        };
    }
}
