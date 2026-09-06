using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for SQLite-backed app logs, a bounded live mihomo log window, and cleanup actions.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="LogsViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Loads and mutates log storage and opens a page-owned mihomo log WebSocket.
/// </remarks>
public sealed partial class Logs : Page
{
    private static readonly TimeSpan SearchDebounceDelay =
        TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan CleanupPreviewDebounceDelay =
        TimeSpan.FromMilliseconds(150);

    /// <summary>Bindable view model for this page.</summary>
    private readonly LogsViewModel _viewModel;

    private readonly PageLoadSession _loadSession;

    private readonly PageLoadSession _runtimeLogStreamSession;

    private readonly PageOperationSession _pageOperations;

    private readonly Func<string, string> _getString;

    private readonly IApplicationErrorSink _errorSink;

    private readonly Action _navigateBack;

    private bool _isLoaded;
    private bool _cleanupPending;
    private int _visit;

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Logs(LogsPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _getString = dependencies.GetString;
        _errorSink = dependencies.ErrorSink;
        _loadSession = new PageLoadSession(_errorSink, "logs-load");
        _runtimeLogStreamSession = new PageLoadSession(_errorSink, "logs-stream");
        _pageOperations = new PageOperationSession(_errorSink, "logs-cleanup");
        _navigateBack = dependencies.NavigateBack;
        _viewModel.SetSourceFilter(dependencies.InitialSourceFilter);
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }
        _isLoaded = true;
        int visit = ++_visit;
        await RunObservedPageEventAsync(
            "logs-page-load",
            async () =>
            {
                await DrainPageOperationsAsync();
                if (!_isLoaded || visit != _visit)
                {
                    return;
                }
                await Task.WhenAll(
                    _runtimeLogStreamSession.RunAsync(_viewModel.WatchRuntimeLogsAsync),
                    _loadSession.RunAsync(_viewModel.LoadAsync));
            });
    }

    private async void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        ++_visit;
        _loadSession.Cancel();
        _runtimeLogStreamSession.Cancel();
        _pageOperations.Cancel();
        await RunObservedPageEventAsync("logs-page-unload", DrainPageOperationsAsync);
    }

    private async void LogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoaded && sender is TextBox textBox)
        {
            if (StringComparer.Ordinal.Equals(_viewModel.SearchText, textBox.Text))
            {
                return;
            }

            _viewModel.ApplySearchText(textBox.Text);
            await RunObservedPageEventAsync(
                "logs-search",
                () => _loadSession.RunAsync(
                    _viewModel.LoadAsync,
                    SearchDebounceDelay));
        }
    }

    private async void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }
        if (sender is ComboBox { SelectedItem: string selectedLevel } comboBox && ReferenceEquals(comboBox, LevelFilterBox))
        {
            if (StringComparer.Ordinal.Equals(_viewModel.SelectedLevelFilter, selectedLevel))
            {
                return;
            }

            _viewModel.SelectedLevelFilter = selectedLevel;
            await RunObservedPageEventAsync(
                "logs-level-filter",
                () => _loadSession.RunAsync(_viewModel.LoadAsync));
            return;
        }

        if (sender is ComboBox { SelectedItem: string selectedCategory } categoryBox && ReferenceEquals(categoryBox, CategoryFilterBox))
        {
            if (StringComparer.Ordinal.Equals(_viewModel.SelectedCategoryFilter, selectedCategory))
            {
                return;
            }

            _viewModel.SelectedCategoryFilter = selectedCategory;
            await RunObservedPageEventAsync(
                "logs-category-filter",
                () => _loadSession.RunAsync(_viewModel.LoadAsync));
        }
    }

    private async void RefreshLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }
        await RunObservedPageEventAsync(
            "logs-refresh",
            () => _loadSession.RunAsync(_viewModel.LoadAsync));
    }

    /// <summary>Requests semantic back navigation from the owning shell.</summary>
    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _navigateBack();
    }

    /// <summary>Handles cleanup entry clicks by showing available cleanup modes and their parameters.</summary>
    /// <param name="sender">The clicked cleanup command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void CleanupButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded || _cleanupPending)
        {
            return;
        }
        _cleanupPending = true;
        Button? button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }
        try
        {
            await _pageOperations.RunAsync(ShowCleanupDialogAsync);
        }
        finally
        {
            _cleanupPending = false;
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private async Task ShowCleanupDialogAsync(CancellationToken pageToken)
    {
        PageLoadSession previewSession = new(_errorSink, "logs-cleanup-preview");
        bool dialogOpen = true;
        ComboBox cleanupModeBox = new()
        {
            SelectedIndex = 0,
        };
        cleanupModeBox.Items.Add(_getString("Logs.Cleanup.Mode.ByDate"));
        cleanupModeBox.Items.Add(_getString("Logs.Cleanup.Mode.BySize"));
        cleanupModeBox.Items.Add(_getString("Logs.Cleanup.Mode.ByCount"));
        cleanupModeBox.Items.Add(_getString("Logs.Cleanup.Mode.All"));
        cleanupModeBox.Items.Add($"{_viewModel.LevelFilterLabelText} / {_viewModel.CategoryFilterLabelText}");

        NumberBox parameterBox = new()
        {
            Header = _getString("Logs.Cleanup.Parameter.KeepDays"),
            Minimum = 1,
            Maximum = 3650,
            Value = 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        TextBlock descriptionText = new()
        {
            Text = _getString("Logs.Cleanup.Description.ByDate"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        ComboBox levelBox = new()
        {
            Header = _viewModel.LevelFilterLabelText,
            ItemsSource = _viewModel.LevelFilterOptions,
            SelectedItem = _viewModel.SelectedLevelFilter,
            IsEnabled = false,
        };
        ComboBox categoryBox = new()
        {
            Header = _viewModel.CategoryFilterLabelText,
            ItemsSource = _viewModel.CategoryFilterOptions,
            SelectedItem = _viewModel.SelectedCategoryFilter,
            IsEnabled = false,
        };
        TextBlock previewText = new()
        {
            Text = _viewModel.CleanupPreviewPlaceholderText,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
        StackPanel content = new()
        {
            Spacing = 12,
            MinWidth = 520,
            MaxWidth = 640,
        };
        content.Children.Add(cleanupModeBox);
        content.Children.Add(parameterBox);
        content.Children.Add(levelBox);
        content.Children.Add(categoryBox);
        content.Children.Add(descriptionText);
        content.Children.Add(previewText);

        async Task UpdatePreviewAsync(Action? updateEditor = null)
        {
            if (!dialogOpen || pageToken.IsCancellationRequested)
            {
                return;
            }
            try
            {
                updateEditor?.Invoke();
                int selectedIndex = cleanupModeBox.SelectedIndex;
                double parameterValue = parameterBox.Value;
                string? levelFilter = levelBox.SelectedItem as string;
                string? categoryFilter = categoryBox.SelectedItem as string;
                previewText.Text = _viewModel.CleanupPreviewPlaceholderText;

                await previewSession.RunAsync(
                    async previewToken =>
                    {
                        string? text = await _viewModel.GetCleanupPreviewTextAsync(
                            selectedIndex,
                            parameterValue,
                            levelFilter,
                            categoryFilter,
                            previewToken);
                        previewToken.ThrowIfCancellationRequested();
                        if (text is not null)
                        {
                            previewText.Text = text;
                        }
                    },
                    CleanupPreviewDebounceDelay,
                    pageToken);
            }
            catch (Exception exception) when (
                ExceptionGraphClassifier.IsCallerCancellation(exception, pageToken))
            {
                // Closing the page dismisses its in-flight preview without changing dialog text.
            }
            catch (Exception exception) when (
                !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                await previewSession.RunAsync(
                    _ => Task.FromException(exception),
                    cancellationToken: CancellationToken.None);
            }
        }

        async void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            await UpdatePreviewAsync(
                () => UpdateCleanupParameterEditor(
                    cleanupModeBox.SelectedIndex,
                    parameterBox,
                    descriptionText,
                    levelBox,
                    categoryBox));
        }
        async void OnParameterChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) =>
            await UpdatePreviewAsync();
        async void OnFilterChanged(object sender, SelectionChangedEventArgs e) =>
            await UpdatePreviewAsync();
        cleanupModeBox.SelectionChanged += OnModeChanged;
        parameterBox.ValueChanged += OnParameterChanged;
        levelBox.SelectionChanged += OnFilterChanged;
        categoryBox.SelectionChanged += OnFilterChanged;

        ThemedContentDialog dialog = new()
        {
            Title = _getString("Logs.Cleanup.Title"),
            Content = content,
            MaxWidth = 720,
            PrimaryButtonText = _getString("Command.Cleanup"),
            CloseButtonText = _getString("Command.Cancel"),
            XamlRoot = XamlRoot,
        };

        ContentDialogResult result;
        Task initialPreview = UpdatePreviewAsync();
        try
        {
            result = await dialog.ShowManagedAsync(pageToken);
        }
        finally
        {
            dialogOpen = false;
            cleanupModeBox.SelectionChanged -= OnModeChanged;
            parameterBox.ValueChanged -= OnParameterChanged;
            levelBox.SelectionChanged -= OnFilterChanged;
            categoryBox.SelectionChanged -= OnFilterChanged;
            previewSession.Cancel();
            await Task.WhenAll(initialPreview, previewSession.DrainAsync());
        }

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        int selectedCleanupMode = cleanupModeBox.SelectedIndex;
        double cleanupParameter = parameterBox.Value;
        string? selectedLevelFilter = levelBox.SelectedItem as string;
        string? selectedCategoryFilter = categoryBox.SelectedItem as string;
        IsEnabled = false;
        try
        {
            _loadSession.Cancel();
            await _loadSession.DrainAsync();
            pageToken.ThrowIfCancellationRequested();
            await _viewModel.ApplyCleanupModeAsync(
                selectedCleanupMode,
                cleanupParameter,
                selectedLevelFilter,
                selectedCategoryFilter,
                pageToken);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    /// <summary>Updates the parameter editor to match the selected cleanup mode.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index.</param>
    /// <param name="parameterBox">Numeric parameter editor. Must not be null.</param>
    /// <param name="descriptionText">Cleanup description text. Must not be null.</param>
    /// <param name="levelBox">Level filter enabled only for filtered cleanup mode.</param>
    /// <param name="categoryBox">Category filter enabled only for filtered cleanup mode.</param>
    private void UpdateCleanupParameterEditor(
        int selectedIndex,
        NumberBox parameterBox,
        TextBlock descriptionText,
        ComboBox levelBox,
        ComboBox categoryBox)
    {
        ArgumentNullException.ThrowIfNull(parameterBox);
        ArgumentNullException.ThrowIfNull(descriptionText);
        ArgumentNullException.ThrowIfNull(levelBox);
        ArgumentNullException.ThrowIfNull(categoryBox);

        levelBox.IsEnabled = selectedIndex == 4;
        categoryBox.IsEnabled = selectedIndex == 4;

        switch (selectedIndex)
        {
            case 0:
                parameterBox.IsEnabled = true;
                parameterBox.Header = _getString("Logs.Cleanup.Parameter.KeepDays");
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 3650;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 30 : Math.Clamp(parameterBox.Value, 1, 3650);
                descriptionText.Text = _getString("Logs.Cleanup.Description.ByDate");
                break;
            case 1:
                parameterBox.IsEnabled = true;
                parameterBox.Header = _getString("Logs.Cleanup.Parameter.TargetSize");
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 102400;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 10 : Math.Clamp(parameterBox.Value, 1, 102400);
                descriptionText.Text = _getString("Logs.Cleanup.Description.BySize");
                break;
            case 2:
                parameterBox.IsEnabled = true;
                parameterBox.Header = _getString("Logs.Cleanup.Parameter.KeepLogCount");
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 10000000;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 1000 : Math.Clamp(parameterBox.Value, 1, 10000000);
                descriptionText.Text = _getString("Logs.Cleanup.Description.ByCount");
                break;
            case 3:
                parameterBox.IsEnabled = false;
                parameterBox.Header = _getString("Logs.Cleanup.Parameter.None");
                descriptionText.Text = _getString("Logs.Cleanup.Description.All");
                break;
            case 4:
                parameterBox.IsEnabled = false;
                parameterBox.Header = _getString("Logs.Cleanup.Parameter.None");
                descriptionText.Text = $"{_getString("Logs.Filter.Level")} / {_getString("Logs.Filter.Category")}";
                break;
        }
    }

    private Task DrainPageOperationsAsync() => Task.WhenAll(
        _loadSession.DrainAsync(),
        _runtimeLogStreamSession.DrainAsync(),
        _pageOperations.DrainAsync());

    private async Task RunObservedPageEventAsync(
        string operationName,
        Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation();
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportUnexpectedAsync(operationName, exception);
        }
    }

    private async Task ReportUnexpectedAsync(
        string operationName,
        Exception exception)
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
            // Stable existing UI state is the final fallback when diagnostics also fail.
        }
    }
}
