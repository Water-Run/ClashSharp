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
using Microsoft.UI.Xaml.Navigation;

namespace ClashSharp.View;

/// <summary>Page reserved for SQLite-backed logs, storage usage, and cleanup actions.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="LogsViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Loads and mutates log storage only through explicit UI lifecycle and commands.
/// </remarks>
public sealed partial class Logs : Page
{
    private static readonly TimeSpan SearchDebounceDelay =
        TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan CleanupPreviewDebounceDelay =
        TimeSpan.FromMilliseconds(150);

    /// <summary>Bindable view model for this page.</summary>
    private readonly LogsViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();

    private readonly PageLoadSession _cleanupPreviewSession = new();

    private readonly Func<string, string> _getString;

    private readonly IApplicationErrorSink _errorSink;

    private CancellationTokenSource _pageLifetime = new();

    /// <summary>Initializes the logs page without reading persistent log storage.</summary>
    public Logs()
        : this(LogsPageComposition.Create())
    {
    }

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Logs(LogsPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _getString = dependencies.GetString;
        _errorSink = dependencies.ErrorSink;
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ResetPageLifetime();
        await RunObservedPageEventAsync(
            "logs-page-load",
            pageToken => RunLatestPageOperationAsync(
                _loadSession,
                _viewModel.LoadAsync,
                pageToken));
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _pageLifetime.Cancel();
        _loadSession.Cancel();
        _cleanupPreviewSession.Cancel();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _viewModel.SetSourceFilter(e.Parameter as string);
    }

    private async void LogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            if (StringComparer.Ordinal.Equals(_viewModel.SearchText, textBox.Text))
            {
                return;
            }

            _viewModel.ApplySearchText(textBox.Text);
            await RunObservedPageEventAsync(
                "logs-search",
                pageToken => RunLatestPageOperationAsync(
                    _loadSession,
                    _viewModel.LoadAsync,
                    pageToken,
                    SearchDebounceDelay));
        }
    }

    private async void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string selectedLevel } comboBox && ReferenceEquals(comboBox, LevelFilterBox))
        {
            if (StringComparer.Ordinal.Equals(_viewModel.SelectedLevelFilter, selectedLevel))
            {
                return;
            }

            _viewModel.SelectedLevelFilter = selectedLevel;
            await RunObservedPageEventAsync(
                "logs-level-filter",
                pageToken => RunLatestPageOperationAsync(
                    _loadSession,
                    _viewModel.LoadAsync,
                    pageToken));
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
                pageToken => RunLatestPageOperationAsync(
                    _loadSession,
                    _viewModel.LoadAsync,
                    pageToken));
        }
    }

    private async void RefreshLogsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunObservedPageEventAsync(
            "logs-refresh",
            pageToken => RunLatestPageOperationAsync(
                _loadSession,
                _viewModel.LoadAsync,
                pageToken));
    }

    /// <summary>Navigates back to the previous page, falling back to statistics.</summary>
    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
            return;
        }

        Frame.Navigate(typeof(Statistics));
    }

    /// <summary>Handles cleanup entry clicks by showing available cleanup modes and their parameters.</summary>
    /// <param name="sender">The clicked cleanup command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void CleanupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunObservedPageEventAsync(
            "logs-cleanup-presentation",
            ShowCleanupDialogAsync);
    }

    private async Task ShowCleanupDialogAsync(CancellationToken pageToken)
    {
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
            try
            {
                updateEditor?.Invoke();
                int selectedIndex = cleanupModeBox.SelectedIndex;
                double parameterValue = parameterBox.Value;
                string? levelFilter = levelBox.SelectedItem as string;
                string? categoryFilter = categoryBox.SelectedItem as string;
                previewText.Text = _viewModel.CleanupPreviewPlaceholderText;

                await RunLatestPageOperationAsync(
                    _cleanupPreviewSession,
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
                    pageToken,
                    CleanupPreviewDebounceDelay);
            }
            catch (Exception exception) when (
                ExceptionGraphClassifier.IsCallerCancellation(exception, pageToken))
            {
                // Closing the page dismisses its in-flight preview without changing dialog text.
            }
            catch (Exception exception) when (
                !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                await ReportUnexpectedAsync("logs-cleanup-preview-presentation", exception);
            }
        }

        cleanupModeBox.SelectionChanged += async (_, _) =>
        {
            await UpdatePreviewAsync(
                () => UpdateCleanupParameterEditor(
                    cleanupModeBox.SelectedIndex,
                    parameterBox,
                    descriptionText,
                    levelBox,
                    categoryBox));
        };
        parameterBox.ValueChanged += async (_, _) => await UpdatePreviewAsync();
        levelBox.SelectionChanged += async (_, _) => await UpdatePreviewAsync();
        categoryBox.SelectionChanged += async (_, _) => await UpdatePreviewAsync();

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
        try
        {
            result = await dialog.ShowManagedAsync(pageToken);
        }
        finally
        {
            _cleanupPreviewSession.Cancel();
        }

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        int selectedCleanupMode = cleanupModeBox.SelectedIndex;
        double cleanupParameter = parameterBox.Value;
        string? selectedLevelFilter = levelBox.SelectedItem as string;
        string? selectedCategoryFilter = categoryBox.SelectedItem as string;
        await RunLatestPageOperationAsync(
            _loadSession,
            cleanupToken => _viewModel.ApplyCleanupModeAsync(
                selectedCleanupMode,
                cleanupParameter,
                selectedLevelFilter,
                selectedCategoryFilter,
                cleanupToken),
            pageToken);
    }

    /// <summary>Updates the parameter editor to match the selected cleanup mode.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index.</param>
    /// <param name="parameterBox">Numeric parameter editor. Must not be null.</param>
    /// <param name="descriptionText">Cleanup description text. Must not be null.</param>
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

    private void ResetPageLifetime()
    {
        CancellationTokenSource previousLifetime = _pageLifetime;
        _pageLifetime = new CancellationTokenSource();
        previousLifetime.Cancel();
        previousLifetime.Dispose();
    }

    private async Task RunObservedPageEventAsync(
        string operationName,
        Func<CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);
        CancellationToken pageToken = _pageLifetime.Token;
        try
        {
            await operation(pageToken);
        }
        catch (Exception exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, pageToken))
        {
            // Page teardown owns cancellation and leaves the last stable bound state intact.
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportUnexpectedAsync(operationName, exception);
        }
    }

    private static Task RunLatestPageOperationAsync(
        PageLoadSession session,
        Func<CancellationToken, Task> operation,
        CancellationToken pageToken,
        TimeSpan debounceDelay = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);
        return session.RunAsync(
            async operationToken =>
            {
                using CancellationTokenSource linkedCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        pageToken,
                        operationToken);
                await operation(linkedCancellation.Token);
            },
            debounceDelay);
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
