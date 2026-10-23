using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Components;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization.NumberFormatting;

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
    private readonly PageDataChangeSession _dataChanges;

    private readonly Func<string, string> _getString;

    private readonly IApplicationErrorSink _errorSink;

    private readonly Action _navigateBack;

    private bool _isLoaded;
    private bool _cleanupPending;
    private bool _synchronizingFilters;
    private bool _compactFilters;
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
        _dataChanges = new(dependencies.SubscribeToDataChanges, action => DispatcherQueue.TryEnqueue(() => action()),
            [_loadSession.Cancel, _runtimeLogStreamSession.Cancel, _pageOperations.Cancel], ReloadChangedDataAsync, _errorSink, "logs-data-change");
        _navigateBack = dependencies.NavigateBack;
        _viewModel.SetSourceFilter(dependencies.InitialSourceFilter);
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Keeps search and localized filter choices readable when the navigation leaves a narrow content area.</summary>
    private void LogFiltersPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 820;
        if (_compactFilters == compact) { return; }
        _compactFilters = compact;
        Grid.SetColumnSpan(LogSearchBox, compact ? 4 : 1);
        Grid.SetRow(LevelFilterBox, compact ? 1 : 0);
        Grid.SetColumn(LevelFilterBox, compact ? 0 : 1);
        Grid.SetRow(CategoryFilterBox, compact ? 1 : 0);
        Grid.SetColumn(CategoryFilterBox, compact ? 1 : 2);
        Grid.SetColumnSpan(CategoryFilterBox, compact ? 2 : 1);
        Grid.SetRow(RefreshLogsButton, compact ? 1 : 0);
        LogFiltersPanel.ColumnDefinitions[1].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(160);
        LogFiltersPanel.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(180);
        LogFiltersPanel.RowSpacing = compact ? 12 : 0;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }
        _isLoaded = true;
        _dataChanges.Start();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        SynchronizeFilterControls();
        int visit = ++_visit;
        await RunObservedPageEventAsync(
            "logs-page-load",
            async () =>
            {
                await Task.WhenAll(DrainPageOperationsAsync(), _dataChanges.DrainAsync());
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
        _dataChanges.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ++_visit;
        _loadSession.Cancel();
        _runtimeLogStreamSession.Cancel();
        _pageOperations.Cancel();
        await RunObservedPageEventAsync("logs-page-unload", () => Task.WhenAll(DrainPageOperationsAsync(), _dataChanges.DrainAsync()));
    }

    private async Task ReloadChangedDataAsync(CancellationToken cancellationToken)
    {
        await DrainPageOperationsAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await _loadSession.RunAsync(_viewModel.ReloadForDataChangeAsync, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // The read session owns the stream; this refresh completes after the new snapshot is installed.
        _ = _runtimeLogStreamSession.RunAsync(_viewModel.WatchRuntimeLogsAsync, cancellationToken: CancellationToken.None);
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
        if (!_isLoaded || _synchronizingFilters || _viewModel.IsUpdatingFilterOptions)
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LogsViewModel.SelectedLevelFilterIndex)
            or nameof(LogsViewModel.SelectedCategoryFilterIndex))
        {
            SynchronizeFilterControls();
        }
    }

    /// <summary>Restores native selection after item changes, even when the desired index is unchanged.</summary>
    private void SynchronizeFilterControls()
    {
        if (!_isLoaded || _viewModel.IsUpdatingFilterOptions)
        {
            return;
        }

        _synchronizingFilters = true;
        try
        {
            LevelFilterBox.SelectedIndex = _viewModel.SelectedLevelFilterIndex;
            CategoryFilterBox.SelectedIndex = _viewModel.SelectedCategoryFilterIndex;
        }
        finally
        {
            _synchronizingFilters = false;
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
        if (!_isLoaded || _dataChanges.IsInvalidated || _cleanupPending)
        {
            return;
        }
        _cleanupPending = true;
        ButtonBase? button = sender as ButtonBase;
        FocusState focusState = button?.FocusState ?? FocusState.Programmatic;
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
                if (_isLoaded && button.IsLoaded)
                {
                    button.Focus(focusState == FocusState.Unfocused ? FocusState.Programmatic : focusState);
                }
            }
        }
    }

    private async Task ShowCleanupDialogAsync(CancellationToken pageToken)
    {
        LogsViewModel.CleanupFilterSnapshot filters = _viewModel.CaptureCleanupFilters();
        PageLoadSession previewSession = new(_errorSink, "logs-cleanup-preview");
        bool dialogOpen = true;
        ComboBox cleanupModeBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        List<RetainedComboBoxOption> modes = [];
        for (int index = 0; index < 5; index++)
        {
            RetainedComboBoxOption option = new(string.Empty, index);
            modes.Add(option);
            cleanupModeBox.Items.Add(option.Item);
        }
        cleanupModeBox.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(cleanupModeBox, "LogCleanupModeBox");

        NumberBox parameterBox = new()
        {
            Header = _getString("Logs.Cleanup.Parameter.KeepDays"),
            Minimum = 1,
            Maximum = 3650,
            Value = 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            NumberFormatter = new DecimalFormatter
            {
                IntegerDigits = 1,
                FractionDigits = 0,
                NumberRounder = new IncrementNumberRounder
                {
                    Increment = 1,
                    RoundingAlgorithm = RoundingAlgorithm.RoundHalfToEven,
                },
            },
        };
        AutomationProperties.SetAutomationId(parameterBox, "LogCleanupParameterBox");
        TextBlock descriptionText = new()
        {
            Text = _getString("Logs.Cleanup.Description.ByDate"),
            TextWrapping = TextWrapping.Wrap,
        };
        ComboBox levelBox = new()
        {
            Header = _viewModel.LevelFilterLabelText,
            Visibility = Visibility.Collapsed,
        };
        ComboBox categoryBox = new()
        {
            Header = _viewModel.CategoryFilterLabelText,
            Visibility = Visibility.Collapsed,
        };
        List<RetainedComboBoxOption> levelOptions = CreateFilterOptions(levelBox, filters.LevelOptions, filters.SelectedLevel);
        List<RetainedComboBoxOption> categoryOptions = CreateFilterOptions(categoryBox, filters.CategoryOptions, filters.SelectedCategory);
        AutomationProperties.SetAutomationId(levelBox, "LogCleanupLevelBox");
        AutomationProperties.SetAutomationId(categoryBox, "LogCleanupCategoryBox");
        TextBlock previewText = new()
        {
            Text = _viewModel.CleanupPreviewPlaceholderText,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        AutomationProperties.SetAutomationId(previewText, "LogCleanupPreviewText");
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

        ThemedContentDialog dialog = new()
        {
            Title = _getString("Logs.Cleanup.Title"),
            Content = content,
            PrimaryButtonText = _getString("Command.Cleanup"),
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
        };
        // Size the dialog surface without constraining the full-window popup host.
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        TextBox? parameterInput = null;
        double previewParameter = double.NaN;
        LogsViewModel.CleanupPreview? currentPreview = null;
        LogsViewModel.CleanupPreview? acceptedPreview = null;
        string previewMessageKey = "Logs.Cleanup.Preview.Loading";

        static List<RetainedComboBoxOption> CreateFilterOptions(ComboBox box, IReadOnlyList<string> labels, string selected)
        {
            List<RetainedComboBoxOption> options = [];
            foreach (string label in labels)
            {
                RetainedComboBoxOption option = new(label, label);
                options.Add(option);
                box.Items.Add(option.Item);
                if (StringComparer.Ordinal.Equals(label, selected)) { box.SelectedItem = option.Item; }
            }
            return options;
        }

        static string? SelectedFilter(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        void RefreshPreviewText()
        {
            previewText.Text = currentPreview is not null ? _viewModel.FormatCleanupPreview(currentPreview)
                : previewMessageKey == "Logs.Cleanup.Parameter.Required"
                    ? string.Format(CultureInfo.CurrentCulture, _getString(previewMessageKey), parameterBox.Minimum, parameterBox.Maximum)
                    : _getString(previewMessageKey);
        }

        dialog.Localize(() =>
        {
            dialog.Title = _getString("Logs.Cleanup.Title");
            dialog.PrimaryButtonText = _getString("Command.Cleanup");
            dialog.CloseButtonText = _getString("Command.Cancel");
            string[] modeKeys = ["Logs.Cleanup.Mode.ByDate", "Logs.Cleanup.Mode.BySize", "Logs.Cleanup.Mode.ByCount", "Logs.Cleanup.Mode.All"];
            for (int index = 0; index < modeKeys.Length; index++) { modes[index].Text = _getString(modeKeys[index]); }
            modes[4].Text = $"{_viewModel.LevelFilterLabelText} / {_viewModel.CategoryFilterLabelText}";
            levelBox.Header = _viewModel.LevelFilterLabelText;
            categoryBox.Header = _viewModel.CategoryFilterLabelText;
            IReadOnlyDictionary<string, string> levels = _viewModel.GetCleanupFilterLabels(filters, levels: true);
            IReadOnlyDictionary<string, string> categories = _viewModel.GetCleanupFilterLabels(filters, levels: false);
            foreach (RetainedComboBoxOption option in levelOptions) { option.Text = levels[(string)option.Item.Tag]; }
            foreach (RetainedComboBoxOption option in categoryOptions) { option.Text = categories[(string)option.Item.Tag]; }
            UpdateCleanupParameterText(cleanupModeBox.SelectedIndex, parameterBox, descriptionText, filters);
            RefreshPreviewText();
        });

        bool TryReadParameter(out double value)
        {
            // NumberBox.Text and Value still contain the last committed value while
            // InputBox is being edited. Read the visible draft for destructive actions.
            string? text = parameterInput?.Text;
            double? parsed = string.IsNullOrWhiteSpace(text)
                ? null
                : ((INumberParser)parameterBox.NumberFormatter).ParseDouble(text.Trim());
            value = parsed ?? double.NaN;
            return double.IsFinite(value) &&
                value >= parameterBox.Minimum && value <= parameterBox.Maximum &&
                value == Math.Round(value);
        }

        void RequireCleanupParameter()
        {
            previewSession.Cancel();
            currentPreview = null;
            previewMessageKey = "Logs.Cleanup.Parameter.Required";
            dialog.IsPrimaryButtonEnabled = false;
            RefreshPreviewText();
        }

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
                double parameterValue = double.NaN;
                string? levelFilter = SelectedFilter(levelBox);
                string? categoryFilter = SelectedFilter(categoryBox);
                currentPreview = null;
                previewMessageKey = "Logs.Cleanup.Preview.Loading";
                RefreshPreviewText();
                dialog.IsPrimaryButtonEnabled = false;
                previewParameter = double.NaN;
                if (selectedIndex is >= 0 and <= 2)
                {
                    if (parameterInput is null)
                    {
                        previewSession.Cancel();
                        return;
                    }
                    if (!TryReadParameter(out parameterValue))
                    {
                        RequireCleanupParameter();
                        return;
                    }
                }

                await previewSession.RunAsync(
                    async previewToken =>
                    {
                        LogsViewModel.CleanupPreview? preview = await _viewModel.GetCleanupPreviewAsync(
                            selectedIndex,
                            parameterValue,
                            levelFilter,
                            categoryFilter,
                            previewToken,
                            filters);
                        previewToken.ThrowIfCancellationRequested();
                        currentPreview = preview;
                        previewMessageKey = "Logs.Cleanup.Preview.Failed";
                        RefreshPreviewText();
                        previewParameter = parameterValue;
                        dialog.IsPrimaryButtonEnabled = preview is not null;
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
                currentPreview = null;
                previewMessageKey = "Logs.Cleanup.Preview.Failed";
                dialog.IsPrimaryButtonEnabled = false;
                RefreshPreviewText();
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
                    categoryBox,
                    filters));
        }
        async void OnParameterTextChanging(TextBox sender, TextBoxTextChangingEventArgs e) =>
            await UpdatePreviewAsync();
        async void OnParameterLoaded(object sender, RoutedEventArgs e)
        {
            if (parameterInput is not null)
            {
                return;
            }
            parameterInput = FindNumberBoxInput(parameterBox);
            if (parameterInput is null)
            {
                previewSession.Cancel();
                currentPreview = null;
                previewMessageKey = "Logs.Cleanup.Preview.Failed";
                RefreshPreviewText();
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }
            parameterInput.TextChanging += OnParameterTextChanging;
            await UpdatePreviewAsync();
        }
        async void OnFilterChanged(object sender, SelectionChangedEventArgs e) =>
            await UpdatePreviewAsync();
        async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs e)
        {
            if (cleanupModeBox.SelectedIndex is >= 0 and <= 2)
            {
                if (!TryReadParameter(out double value))
                {
                    e.Cancel = true;
                    RequireCleanupParameter();
                    return;
                }
                if (value != previewParameter || !dialog.IsPrimaryButtonEnabled)
                {
                    e.Cancel = true;
                    await UpdatePreviewAsync();
                    return;
                }
            }
            if (currentPreview is null || !dialog.IsPrimaryButtonEnabled) { e.Cancel = true; return; }
            acceptedPreview = currentPreview;
        }
        cleanupModeBox.SelectionChanged += OnModeChanged;
        parameterBox.Loaded += OnParameterLoaded;
        levelBox.SelectionChanged += OnFilterChanged;
        categoryBox.SelectionChanged += OnFilterChanged;
        dialog.PrimaryButtonClick += OnPrimaryButtonClick;

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
            parameterBox.Loaded -= OnParameterLoaded;
            if (parameterInput is not null)
            {
                parameterInput.TextChanging -= OnParameterTextChanging;
            }
            levelBox.SelectionChanged -= OnFilterChanged;
            categoryBox.SelectionChanged -= OnFilterChanged;
            dialog.PrimaryButtonClick -= OnPrimaryButtonClick;
            previewSession.Cancel();
            await Task.WhenAll(initialPreview, previewSession.DrainAsync());
        }

        if (result != ContentDialogResult.Primary || acceptedPreview is null)
        {
            return;
        }

        IsEnabled = false;
        try
        {
            _loadSession.Cancel();
            await _loadSession.DrainAsync();
            pageToken.ThrowIfCancellationRequested();
            await _viewModel.ApplyCleanupModeAsync(
                acceptedPreview,
                pageToken);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static TextBox? FindNumberBoxInput(DependencyObject root)
    {
        if (root is TextBox { Name: "InputBox" } input)
        {
            return input;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindNumberBoxInput(VisualTreeHelper.GetChild(root, index)) is TextBox child)
            {
                return child;
            }
        }
        return null;
    }

    /// <summary>Updates the parameter editor to match the selected cleanup mode.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index.</param>
    /// <param name="parameterBox">Numeric parameter editor. Must not be null.</param>
    /// <param name="descriptionText">Cleanup description text. Must not be null.</param>
    /// <param name="levelBox">Level filter enabled only for filtered cleanup mode.</param>
    /// <param name="categoryBox">Category filter enabled only for filtered cleanup mode.</param>
    /// <param name="filters">The captured unit and filter choices for this editor.</param>
    private void UpdateCleanupParameterEditor(
        int selectedIndex,
        NumberBox parameterBox,
        TextBlock descriptionText,
        ComboBox levelBox,
        ComboBox categoryBox,
        LogsViewModel.CleanupFilterSnapshot filters)
    {
        ArgumentNullException.ThrowIfNull(parameterBox);
        ArgumentNullException.ThrowIfNull(descriptionText);
        ArgumentNullException.ThrowIfNull(levelBox);
        ArgumentNullException.ThrowIfNull(categoryBox);

        levelBox.Visibility = selectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        categoryBox.Visibility = levelBox.Visibility;
        parameterBox.Visibility = selectedIndex is >= 0 and <= 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCleanupParameterText(selectedIndex, parameterBox, descriptionText, filters);

        switch (selectedIndex)
        {
            case 0:
                parameterBox.IsEnabled = true;
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 3650;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 30 : Math.Clamp(parameterBox.Value, 1, 3650);
                break;
            case 1:
                parameterBox.IsEnabled = true;
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 102400;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 10 : Math.Clamp(parameterBox.Value, 1, 102400);
                break;
            case 2:
                parameterBox.IsEnabled = true;
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 10000000;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 1000 : Math.Clamp(parameterBox.Value, 1, 10000000);
                break;
            case 3:
                parameterBox.IsEnabled = false;
                break;
            case 4:
                parameterBox.IsEnabled = false;
                break;
        }
    }

    private void UpdateCleanupParameterText(int selectedIndex, NumberBox parameterBox,
        TextBlock descriptionText, LogsViewModel.CleanupFilterSnapshot filters)
    {
        parameterBox.Header = selectedIndex switch
        {
            0 => _getString("Logs.Cleanup.Parameter.KeepDays"),
            1 => _viewModel.GetCleanupSizeParameterText(filters),
            2 => _getString("Logs.Cleanup.Parameter.KeepLogCount"),
            _ => _getString("Logs.Cleanup.Parameter.None"),
        };
        descriptionText.Text = selectedIndex switch
        {
            0 => _getString("Logs.Cleanup.Description.ByDate"),
            1 => _getString("Logs.Cleanup.Description.BySize"),
            2 => _getString("Logs.Cleanup.Description.ByCount"),
            3 => _getString("Logs.Cleanup.Description.All"),
            _ => $"{_viewModel.LevelFilterLabelText} / {_viewModel.CategoryFilterLabelText}",
        };
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
