using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashSharp.View;

/// <summary>Page for managing subscription configuration profiles.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="ProfilesViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates file pickers and delegates selected file paths to the view model.
/// </remarks>
public sealed partial class Profiles : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly ProfilesViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();

    private readonly PageOperationSession _operations;
    private readonly PageDataChangeSession _dataChanges;

    private bool _isLoaded;

    private int _visit;

    private readonly Func<string, string> _getString;

    private readonly Action _reportFilePickerUnavailable;

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Profiles(ProfilesPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _getString = dependencies.GetString;
        _reportFilePickerUnavailable = dependencies.ReportFilePickerUnavailable;
        _operations = new PageOperationSession(dependencies.ErrorSink, "profiles-page-action");
        _dataChanges = new(dependencies.SubscribeToDataChanges, action => DispatcherQueue.TryEnqueue(() => action()),
            [_loadSession.Cancel, _operations.Cancel], ReloadChangedDataAsync, dependencies.ErrorSink, "profiles-data-change");
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        int visit = ++_visit;
        _isLoaded = true;
        _dataChanges.Start();
        await _operations.DrainAsync();
        await _dataChanges.DrainAsync();
        if (!_isLoaded || visit != _visit)
        {
            return;
        }

        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _dataChanges.Stop();
        ++_visit;
        _loadSession.Cancel();
        _operations.Cancel();
    }

    private async Task ReloadChangedDataAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_loadSession.DrainAsync(), _operations.DrainAsync());
        cancellationToken.ThrowIfCancellationRequested();
        await _loadSession.RunAsync(_viewModel.ReloadForDataChangeAsync, cancellationToken: cancellationToken);
    }

    /// <summary>Shows a native file picker and imports the selected profile file.</summary>
    /// <param name="sender">Command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            if (!TryCreateProfileFilePicker(out FileOpenPicker picker))
            {
                return;
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (file is not null)
            {
                await _viewModel.ImportProfileCommand.ExecuteObservedAsync(
                    file.Path,
                    cancellationToken);
            }
        });
    }

    private async void ImportSubscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(token => _viewModel.ImportSubscriptionCommand.ExecuteObservedAsync(null, token));
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = _viewModel.CanImport && e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!_viewModel.CanImport || !e.DataView.Contains(StandardDataFormats.StorageItems)) { return; }
        await RunPageOperationAsync(async token =>
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            token.ThrowIfCancellationRequested();
            if (items.Count == 1 && items[0] is StorageFile file
                && (file.FileType.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || file.FileType.Equals(".yml", StringComparison.OrdinalIgnoreCase)
                    || file.FileType.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                await _viewModel.ImportProfileCommand.ExecuteObservedAsync(file.Path, token);
            }
            else { _viewModel.ReportInvalidDroppedFile(); }
        });
    }

    private async void ValidateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(
            cancellationToken => _viewModel.ValidateProfileCommand.ExecuteObservedAsync(
                null,
                cancellationToken));
    }

    private async void SetActiveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(
            cancellationToken => _viewModel.SetActiveProfileCommand.ExecuteObservedAsync(
                null,
                cancellationToken));
    }

    private async void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanModifySelectedProfile
            || _viewModel.SelectedProfile is not ConfigurationProfileDisplay selectedProfile)
        {
            return;
        }

        await RunPageOperationAsync(async cancellationToken =>
        {
            TextBox nameBox = new()
            {
                Header = _getString("Profiles.Dialog.Name"),
                Text = selectedProfile.Model.Name,
            };
            InfoBar error = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
            StackPanel content = new() { Spacing = 12 };
            content.Children.Add(nameBox);
            content.Children.Add(error);
            ThemedContentDialog dialog = new()
            {
                Title = _getString("Profiles.Dialog.RenameTitle"),
                Content = content,
                PrimaryButtonText = _getString("Command.Save"),
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            dialog.Localize(() =>
            {
                dialog.Title = _getString("Profiles.Dialog.RenameTitle");
                dialog.PrimaryButtonText = _getString("Command.Save");
                dialog.CloseButtonText = _getString("Command.Cancel");
                nameBox.Header = _getString("Profiles.Dialog.Name");
                if (error.IsOpen) { error.Message = _getString("Profiles.Validation.Name"); }
            });
            dialog.PrimaryButtonClick += (_, args) =>
            {
                bool isInvalid = string.IsNullOrWhiteSpace(nameBox.Text);
                args.Cancel = isInvalid;
                error.IsOpen = isInvalid;
                error.Message = isInvalid ? _getString("Profiles.Validation.Name") : string.Empty;
            };
            ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == ContentDialogResult.Primary)
            {
                await _viewModel.RenameProfileCommand.ExecuteObservedAsync(
                    (selectedProfile.Model.Id, nameBox.Text),
                    cancellationToken);
            }
        });
    }

    private async void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanModifySelectedProfile
            || _viewModel.SelectedProfile is not ConfigurationProfileDisplay selectedProfile)
        {
            return;
        }

        await RunPageOperationAsync(async cancellationToken =>
        {
            ThemedContentDialog dialog = new()
            {
                Title = _getString("Profiles.Dialog.DeleteTitle"),
                Content = selectedProfile.Model.Name,
                PrimaryButtonText = _getString("Command.Delete"),
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            dialog.Localize(() =>
            {
                dialog.Title = _getString("Profiles.Dialog.DeleteTitle");
                dialog.PrimaryButtonText = _getString("Command.Delete");
                dialog.CloseButtonText = _getString("Command.Cancel");
            });
            ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == ContentDialogResult.Primary)
            {
                await _viewModel.DeleteProfileCommand.ExecuteObservedAsync(
                    selectedProfile.Model.Id,
                    cancellationToken);
            }
        });
    }

    private async void ProfileHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedProfile is not ConfigurationProfileDisplay selectedProfile)
        {
            return;
        }

        await RunPageOperationAsync(async cancellationToken =>
        {
            IReadOnlyList<ProfileHistoryPreview>? historyEntries =
                await _viewModel.GetProfileHistoryPreviewsAsync(selectedProfile.Model.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (historyEntries is null) { return; }
            ListView versionList = new()
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 360,
                Padding = new Thickness(0, 4, 0, 4),
            };
            AutomationProperties.SetName(versionList, _getString("Profiles.Dialog.HistoryVersion"));
            List<Action> refreshItems = [];
            foreach (ProfileHistoryPreview preview in historyEntries)
            {
                versionList.Items.Add(CreateHistoryItem(preview, refreshItems));
            }

            if (versionList.Items.Count > 0)
            {
                versionList.SelectedIndex = 0;
            }

            TextBlock description = new()
            {
                Text = _viewModel.HistoryDescriptionText,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ClashSecondaryBodyTextBlockStyle"],
            };
            Border listSurface = new()
            {
                Child = versionList,
                Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = (CornerRadius)Microsoft.UI.Xaml.Application.Current.Resources["OverlayCornerRadius"],
            };
            StackPanel versions = new() { Spacing = 12, MinWidth = 380, MaxWidth = 520 };
            versions.Children.Add(description);
            versions.Children.Add(listSurface);
            FrameworkElement content = versionList.Items.Count > 0
                ? versions
                : new TextBlock { Text = _getString("Profiles.Dialog.HistoryEmpty"), TextWrapping = TextWrapping.Wrap };
            ThemedContentDialog dialog = new()
            {
                Title = _getString("Profiles.Dialog.HistoryTitle"),
                Content = content,
                PrimaryButtonText = _getString("Command.Rollback"),
                CloseButtonText = _getString("Command.Close"),
                IsPrimaryButtonEnabled = SelectedRestorableHistory(versionList) is not null,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            dialog.Localize(() =>
            {
                dialog.Title = _getString("Profiles.Dialog.HistoryTitle");
                dialog.PrimaryButtonText = _getString("Command.Rollback");
                dialog.CloseButtonText = _getString("Command.Close");
                description.Text = _viewModel.HistoryDescriptionText;
                AutomationProperties.SetName(versionList, _getString("Profiles.Dialog.HistoryVersion"));
                if (content is TextBlock empty) { empty.Text = _getString("Profiles.Dialog.HistoryEmpty"); }
                foreach (Action refresh in refreshItems) { refresh(); }
            });
            versionList.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = SelectedRestorableHistory(versionList) is not null;
            ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == ContentDialogResult.Primary
                && SelectedRestorableHistory(versionList) is ProfileHistoryPreview selectedPreview)
            {
                await _viewModel.RollbackProfileCommand.ExecuteObservedAsync(
                    selectedPreview.Entry,
                    cancellationToken);
            }
        });
    }

    /// <summary>Builds one native list row for a retained version: time, source and counts or the restore blocker.</summary>
    /// <param name="preview">Retained version shown by the row.</param>
    /// <param name="refreshItems">Receives the row's localization refresh.</param>
    private ListViewItem CreateHistoryItem(ProfileHistoryPreview preview, List<Action> refreshItems)
    {
        TextBlock timestamp = new() { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"] };
        TextBlock source = new()
        {
            Text = preview.Entry.SourceName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ClashSecondaryCaptionTextBlockStyle"],
        };
        TextBlock detail = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources[preview.CanRestore
                ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush"],
        };
        StackPanel detailLine = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (!preview.CanRestore)
        {
            FontIcon caution = new()
            {
                Glyph = "\uE7BA",
                FontSize = 12,
                Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCautionBrush"],
            };
            AutomationProperties.SetAccessibilityView(caution, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            detailLine.Children.Add(caution);
        }

        detailLine.Children.Add(detail);
        StackPanel row = new() { Spacing = 2, Padding = new Thickness(0, 10, 0, 10) };
        row.Children.Add(timestamp);
        row.Children.Add(source);
        row.Children.Add(detailLine);
        ListViewItem item = new() { Content = row, Tag = preview, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Refresh()
        {
            timestamp.Text = _viewModel.GetHistoryTimestampText(preview);
            detail.Text = _viewModel.GetHistoryDetailText(preview);
            AutomationProperties.SetName(item, _viewModel.GetHistoryEntryText(preview).ReplaceLineEndings(" "));
        }

        Refresh();
        refreshItems.Add(Refresh);
        return item;
    }

    private static ProfileHistoryPreview? SelectedRestorableHistory(ListView versionList) =>
        versionList.SelectedItem is ListViewItem { Tag: ProfileHistoryPreview { CanRestore: true } preview } ? preview : null;

    /// <summary>Makes the double-clicked profile current, the same as the Set as current command.</summary>
    private void ProfilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ConfigurationProfileDisplay profile)
        {
            return;
        }

        ProfilesList.SelectedItem = profile;
        if (!_viewModel.CanActivateSelectedProfile)
        {
            return;
        }

        e.Handled = true;
        SetActiveProfileButton_Click(sender, e);
    }

    /// <summary>Gives keyboard users the same activation as double-click with Enter on the focused row.</summary>
    private void ProfilesList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !_viewModel.CanActivateSelectedProfile)
        {
            return;
        }

        e.Handled = true;
        SetActiveProfileButton_Click(sender, e);
    }

    /// <summary>Creates a native WinUI file picker initialized for the current top-level window.</summary>
    /// <param name="picker">Configured file picker when the main window is available.</param>
    /// <returns>True when the picker is ready; otherwise false.</returns>
    private bool TryCreateProfileFilePicker(out FileOpenPicker picker)
    {
        picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        picker.FileTypeFilter.Add(".txt");

        if (App.MainWindow is null)
        {
            _reportFilePickerUnavailable();
            return false;
        }

        nint hWnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hWnd);
        return true;
    }

    /// <summary>Queues durable page mutations without cancelling an older accepted operation.</summary>
    private Task RunPageOperationAsync(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_isLoaded || _dataChanges.IsInvalidated)
        {
            return Task.CompletedTask;
        }

        return _operations.RunAsync(async cancellationToken =>
        {
            int visit = _visit;
            Control? focusedControl = GetPageFocusedControl();
            FocusState focusState = focusedControl?.FocusState ?? FocusState.Programmatic;
            SetOperationBusy(isBusy: true);
            try
            {
                await operation(cancellationToken);
            }
            finally
            {
                SetOperationBusy(isBusy: false);
                if (_isLoaded && visit == _visit && !cancellationToken.IsCancellationRequested
                    && focusedControl is { IsEnabled: true, Visibility: Visibility.Visible }
                    && focusedControl.XamlRoot == XamlRoot)
                {
                    focusedControl.Focus(focusState);
                }
            }

            if (_isLoaded && visit == _visit && !cancellationToken.IsCancellationRequested
                && _viewModel.SelectedProfile is ConfigurationProfileDisplay selected)
            {
                // Feedback can reduce the viewport after a command. Reveal the retained row
                // using native minimal scrolling without taking focus from the command bar.
                ProfilesList.UpdateLayout();
                ProfilesList.ScrollIntoView(selected, ScrollIntoViewAlignment.Default);
            }
        });
    }

    private Control? GetPageFocusedControl()
    {
        Control? focusedControl = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        for (DependencyObject? element = focusedControl; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, this))
            {
                return focusedControl;
            }
        }

        return null;
    }

    private void SetOperationBusy(bool isBusy)
    {
        ProfileCommands.IsEnabled = !isBusy;
        ProfilesList.IsEnabled = !isBusy;
    }
}
