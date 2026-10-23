using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Components;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using TriggerActionKind = global::ClashSharp.Model.Triggers.TriggerActionKind;

namespace ClashSharp.View;

/// <summary>Forwards WinUI interactions to the asynchronous, lossless trigger editor ViewModels.</summary>
public sealed partial class Triggers : Page
{
    private readonly TriggersViewModel _viewModel;
    private readonly IApplicationErrorSink _errorSink;
    private readonly Action _openLogs;
    private readonly Func<Action, IDisposable> _subscribeToDataChanges;
    private IDisposable? _dataSubscription;
    private Task _dataRefresh = Task.CompletedTask;
    private bool _dataRefreshPending;
    private CancellationTokenSource? _pageLifetime;
    private MenuFlyout? _actionsMenu;

    internal Triggers(TriggersPageDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _errorSink = dependencies.ErrorSink;
        _openLogs = dependencies.OpenLogs;
        _subscribeToDataChanges = dependencies.SubscribeToDataChanges;
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
        CancellationTokenSource lifetime = _pageLifetime;
        _dataSubscription = _subscribeToDataChanges(() => DispatcherQueue.TryEnqueue(() => QueueDataRefresh(lifetime)));
        await AwaitPageOperationAsync(token => _viewModel.RefreshAfterDataChangeAsync(token));
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args)
    {
        WindowDialogCoordinator.TryDismissTransient(XamlRoot, () => _actionsMenu?.Hide());
        _dataSubscription?.Dispose();
        _dataSubscription = null;
        CancellationTokenSource? lifetime = _pageLifetime;
        _pageLifetime = null;
        lifetime?.Cancel();
        await _dataRefresh;
        lifetime?.Dispose();
    }

    private void QueueDataRefresh(CancellationTokenSource lifetime)
    {
        if (!ReferenceEquals(_pageLifetime, lifetime)) { return; }
        _dataRefreshPending = true;
        if (!_dataRefresh.IsCompleted) { return; }
        _dataRefresh = RefreshChangedDataAsync(lifetime);
    }

    private async Task RefreshChangedDataAsync(CancellationTokenSource lifetime)
    {
        while (_dataRefreshPending && ReferenceEquals(_pageLifetime, lifetime))
        {
            _dataRefreshPending = false;
            await AwaitPageOperationAsync(token => _viewModel.RefreshAfterDataChangeAsync(token));
        }
    }

    private void AddTriggerCardButton_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.BeginCreate();
    }

    private void EditTriggerButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: TriggerTaskItemViewModel item } && _viewModel.IsCurrentTask(item))
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
        if (sender is FrameworkElement { Tag: string id, DataContext: TriggerTaskItemViewModel item } && _viewModel.IsCurrentTask(item))
        {
            await AwaitPageOperationAsync(token => _viewModel.MoveTaskAsync(id, -1, token));
        }
    }

    private async void MoveTriggerDownButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string id, DataContext: TriggerTaskItemViewModel item } && _viewModel.IsCurrentTask(item))
        {
            await AwaitPageOperationAsync(token => _viewModel.MoveTaskAsync(id, 1, token));
        }
    }

    private async void DeleteTriggerButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string id, DataContext: TriggerTaskItemViewModel item } || !_viewModel.IsCurrentTask(item))
        {
            return;
        }

        TriggerCatalogVersion expectedVersion = _viewModel.CatalogVersion;
        await AwaitPageOperationAsync(async token =>
        {
            ThemedContentDialog dialog = new()
            {
                Title = _viewModel.DeleteTitleText,
                Content = _viewModel.DeleteMessageText,
                PrimaryButtonText = _viewModel.DeleteText,
                CloseButtonText = _viewModel.CancelText,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            dialog.Localize(() =>
            {
                dialog.Title = _viewModel.DeleteTitleText;
                dialog.Content = _viewModel.DeleteMessageText;
                dialog.PrimaryButtonText = _viewModel.DeleteText;
                dialog.CloseButtonText = _viewModel.CancelText;
            });
            if (await dialog.ShowManagedAsync(token) is ContentDialogResult.Primary)
            {
                await _viewModel.DeleteTaskAsync(id, expectedVersion, token);
            }
        });
    }

    /// <summary>Opens native row actions without reserving space for infrequent commands.</summary>
    private void MoreTriggerActionsButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: TriggerTaskItemViewModel item } button || !_viewModel.IsCurrentTask(item))
        {
            return;
        }

        MenuFlyout menu = new();
        MenuFlyoutItem up = CreateTriggerMenuItem(item, nameof(item.MoveUpText), "\uE70E", MoveTriggerUpButton_Click);
        MenuFlyoutItem down = CreateTriggerMenuItem(item, nameof(item.MoveDownText), "\uE70D", MoveTriggerDownButton_Click);
        MenuFlyoutItem delete = CreateTriggerMenuItem(item, nameof(item.DeleteText), "\uE74D", DeleteTriggerButton_Click);
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(delete);
        menu.Opening += (_, _) =>
        {
            bool current = _viewModel.IsCurrentTask(item);
            up.IsEnabled = current && item.CanMoveUp;
            down.IsEnabled = current && item.CanMoveDown;
            delete.IsEnabled = current;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_actionsMenu, menu)) { _actionsMenu = null; }
        };
        _actionsMenu = menu;
        menu.ShowAt(button);
    }

    /// <summary>Keeps popup commands bound to the same durable row identity checked by their existing handlers.</summary>
    private static MenuFlyoutItem CreateTriggerMenuItem(
        TriggerTaskItemViewModel item, string textProperty, string glyph, RoutedEventHandler handler)
    {
        MenuFlyoutItem command = new()
        {
            Icon = new FontIcon { Glyph = glyph },
            Tag = item.Id,
            DataContext = item,
        };
        command.SetBinding(MenuFlyoutItem.TextProperty, new Binding
        {
            Source = item,
            Path = new PropertyPath(textProperty),
            Mode = BindingMode.OneWay,
        });
        command.Click += handler;
        return command;
    }

    private async void TriggerEnabledToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_viewModel.IsBusy
            || sender is not ToggleSwitch { IsLoaded: true, Tag: TriggerTaskItemViewModel item } toggle
            || !ReferenceEquals(toggle.DataContext, item)
            || !_viewModel.IsCurrentTask(item)
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
        if (editor is null || !editor.CanEdit)
        {
            return;
        }

        await AwaitPageOperationAsync(async cancellationToken =>
        {
            SearchableOptionList options = new() { AllowMultiple = false, MaxListHeight = 360 };
            options.SetOptions(editor.ConditionOptions.Select(option => new SearchableOptionItem(
                option.Value.ToString(), option.Title, string.Empty, option.Description,
                GetConditionGlyph(option.Value), option.Value)));
            void RefreshChoices()
            {
                editor.RefreshLocalization();
                options.SearchPlaceholder = _viewModel.SearchConditionsText;
                options.EmptyText = _viewModel.NoMatchingOptionsText;
                foreach (SearchableOptionItem option in options.Options)
                {
                    TriggerEditorOption<TriggerConditionTemplate> text = editor.ConditionOptions.Single(item => Equals(item.Value, option.Payload));
                    option.UpdateDisplay(text.Title, string.Empty, text.Description);
                }
                options.RefreshFilter();
            }
            if (await ShowOptionDialogAsync(editor, () => _viewModel.ConditionsText, options, RefreshChoices, cancellationToken)
                && options.SelectedOptions.SingleOrDefault()?.Payload is TriggerConditionTemplate template)
            {
                _viewModel.TryAddCondition(editor, template, cancellationToken);
            }
        });
    }

    private async void ChooseTriggerActionButton_Click(object sender, RoutedEventArgs args)
    {
        TriggerEditorViewModel? editor = _viewModel.CurrentEditor;
        if (editor is null || !editor.CanEdit)
        {
            return;
        }

        await AwaitPageOperationAsync(async cancellationToken =>
        {
            SearchableOptionList options = new() { AllowMultiple = false, MaxListHeight = 360 };
            options.SetOptions(editor.ActionOptions.Select(option => new SearchableOptionItem(
                option.Value.ToString(), option.Title, string.Empty, option.Description,
                GetActionGlyph(option.Value), option.Value)));
            void RefreshChoices()
            {
                editor.RefreshLocalization();
                options.SearchPlaceholder = _viewModel.SearchActionsText;
                options.EmptyText = _viewModel.NoMatchingOptionsText;
                foreach (SearchableOptionItem option in options.Options)
                {
                    TriggerEditorOption<TriggerActionKind> text = editor.ActionOptions.Single(item => Equals(item.Value, option.Payload));
                    option.UpdateDisplay(text.Title, string.Empty, text.Description);
                }
                options.RefreshFilter();
            }
            if (await ShowOptionDialogAsync(editor, () => _viewModel.ActionsText, options, RefreshChoices, cancellationToken)
                && options.SelectedOptions.SingleOrDefault()?.Payload is TriggerActionKind kind)
            {
                _viewModel.TryAddAction(editor, kind, cancellationToken);
            }
        });
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

    private async Task<bool> ShowOptionDialogAsync(
        TriggerEditorViewModel editor, Func<string> getTitle, SearchableOptionList content,
        Action refreshChoices, CancellationToken cancellationToken)
    {
        ThemedContentDialog dialog = new()
        {
            Content = content,
            PrimaryButtonText = _viewModel.AddText,
            CloseButtonText = _viewModel.CancelText,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = content.SelectedOptions.Count == 1,
            XamlRoot = XamlRoot,
        };
        using SearchableOptionDialogLayout layout = new(dialog, content);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PropertyChangedEventHandler ownerChanged = (_, _) =>
        {
            if (!ReferenceEquals(_viewModel.CurrentEditor, editor) || editor.IsStale) { lifetime.Cancel(); }
        };
        EventHandler selectionChanged = (_, _) =>
            dialog.IsPrimaryButtonEnabled = content.SelectedOptions.Count == 1;
        content.SelectionChanged += selectionChanged;
        _viewModel.PropertyChanged += ownerChanged;
        editor.PropertyChanged += ownerChanged;
        try
        {
            if (!ReferenceEquals(_viewModel.CurrentEditor, editor) || !editor.CanEdit) { return false; }
            dialog.Localize(() =>
            {
                dialog.Title = getTitle();
                dialog.PrimaryButtonText = _viewModel.AddText;
                dialog.CloseButtonText = _viewModel.CancelText;
                refreshChoices();
                layout.RefreshLayout();
            });
            return await dialog.ShowManagedAsync(lifetime.Token) is ContentDialogResult.Primary;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            content.SelectionChanged -= selectionChanged;
            _viewModel.PropertyChanged -= ownerChanged;
            editor.PropertyChanged -= ownerChanged;
        }
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
