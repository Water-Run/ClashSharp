using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private CancellationTokenSource _pageLifetime = new();

    private readonly Func<string, string> _getString;

    private readonly Action _reportFilePickerUnavailable;

    /// <summary>Initializes the profiles page and its view model.</summary>
    public Profiles()
        : this(ProfilesPageComposition.Create())
    {
    }

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Profiles(ProfilesPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _getString = dependencies.GetString;
        _reportFilePickerUnavailable = dependencies.ReportFilePickerUnavailable;
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_pageLifetime.IsCancellationRequested)
        {
            _pageLifetime = new CancellationTokenSource();
        }

        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadSession.Cancel();
        _pageLifetime.Cancel();
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
        if (_viewModel.SelectedProfile is not ConfigurationProfileDisplay selectedProfile)
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
            ThemedContentDialog dialog = new()
            {
                Title = _getString("Profiles.Dialog.RenameTitle"),
                Content = nameBox,
                PrimaryButtonText = _getString("Command.Save"),
                CloseButtonText = _getString("Command.Cancel"),
                XamlRoot = XamlRoot,
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
        if (_viewModel.SelectedProfile is not ConfigurationProfileDisplay selectedProfile)
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
            IReadOnlyList<ProfileHistoryEntry> historyEntries =
                _viewModel.GetProfileHistory(selectedProfile.Model.Id);
            ComboBox versionBox = new()
            {
                Header = _getString("Profiles.Dialog.HistoryVersion"),
                MinWidth = 420,
            };
            foreach (ProfileHistoryEntry entry in historyEntries)
            {
                versionBox.Items.Add(new ComboBoxItem
                {
                    Content = string.Create(
                        CultureInfo.CurrentCulture,
                        $"{entry.CreatedAt.ToLocalTime():g} · {entry.SourceName} · {entry.NodeCount}/{entry.RuleCount}"),
                    Tag = entry,
                });
            }

            if (versionBox.Items.Count > 0)
            {
                versionBox.SelectedIndex = 0;
            }

            FrameworkElement content = versionBox.Items.Count > 0
                ? versionBox
                : new TextBlock { Text = _getString("Profiles.Dialog.HistoryEmpty") };
            ThemedContentDialog dialog = new()
            {
                Title = _getString("Profiles.Dialog.HistoryTitle"),
                Content = content,
                PrimaryButtonText = _getString("Command.Rollback"),
                CloseButtonText = _getString("Command.Close"),
                IsPrimaryButtonEnabled = versionBox.Items.Count > 0,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == ContentDialogResult.Primary
                && versionBox.SelectedItem is ComboBoxItem { Tag: ProfileHistoryEntry historyEntry })
            {
                await _viewModel.RollbackProfileCommand.ExecuteObservedAsync(
                    historyEntry,
                    cancellationToken);
            }
        });
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
    private async Task RunPageOperationAsync(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationToken cancellationToken = _pageLifetime.Token;
        bool entered = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            entered = true;
            SetOperationBusy(isBusy: true);
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (entered)
            {
                SetOperationBusy(isBusy: false);
                _operationGate.Release();
            }
        }
    }

    private void SetOperationBusy(bool isBusy)
    {
        ImportProfileButton.IsEnabled = !isBusy;
        ValidateProfileButton.IsEnabled = !isBusy;
        SetActiveProfileButton.IsEnabled = !isBusy;
        RenameProfileButton.IsEnabled = !isBusy;
        DeleteProfileButton.IsEnabled = !isBusy;
        ProfileHistoryButton.IsEnabled = !isBusy;
        ProfilesList.IsEnabled = !isBusy;
    }
}
