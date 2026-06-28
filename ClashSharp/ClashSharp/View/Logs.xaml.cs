/*
 * Logs Page
 * Hosts persistent log storage status and delegates storage state to its view model
 *
 * @author: WaterRun
 * @file: View/Logs.xaml.cs
 * @date: 2026-06-17
 */

using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClashSharp.View;

/// <summary>Page reserved for SQLite-backed logs, storage usage, and cleanup actions.</summary>
/// <remarks>
/// Invariants: The page reads storage metadata from <see cref="LogStorageService"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Initializes the log database when constructed.
/// </remarks>
public sealed partial class Logs : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly LogsViewModel _viewModel;

    /// <summary>Initializes the logs page, localized shell text, and storage usage summary.</summary>
    public Logs()
    {
        _viewModel = new(LocalizationService.Instance.GetString, LogStorageService.Instance);
        InitializeComponent();
        DataContext = _viewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _viewModel.SetSourceFilter(e.Parameter as string);
    }

    private void LogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _viewModel.ApplySearchText(textBox.Text);
        }
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string selectedLevel } comboBox && ReferenceEquals(comboBox, LevelFilterBox))
        {
            _viewModel.SelectedLevelFilter = selectedLevel;
            return;
        }

        if (sender is ComboBox { SelectedItem: string selectedCategory } categoryBox && ReferenceEquals(categoryBox, CategoryFilterBox))
        {
            _viewModel.SelectedCategoryFilter = selectedCategory;
        }
    }

    private void RefreshLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshLogs();
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
        ComboBox cleanupModeBox = new()
        {
            SelectedIndex = 0,
        };
        LocalizationService localization = LocalizationService.Instance;
        cleanupModeBox.Items.Add(localization.GetString("Logs.Cleanup.Mode.ByDate"));
        cleanupModeBox.Items.Add(localization.GetString("Logs.Cleanup.Mode.BySize"));
        cleanupModeBox.Items.Add(localization.GetString("Logs.Cleanup.Mode.ByCount"));
        cleanupModeBox.Items.Add(localization.GetString("Logs.Cleanup.Mode.All"));
        cleanupModeBox.Items.Add($"{_viewModel.LevelFilterLabelText} / {_viewModel.CategoryFilterLabelText}");

        NumberBox parameterBox = new()
        {
            Header = localization.GetString("Logs.Cleanup.Parameter.KeepDays"),
            Minimum = 1,
            Maximum = 3650,
            Value = 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        TextBlock descriptionText = new()
        {
            Text = localization.GetString("Logs.Cleanup.Description.ByDate"),
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

        void UpdatePreview()
        {
            previewText.Text = _viewModel.GetCleanupPreviewText(
                cleanupModeBox.SelectedIndex,
                parameterBox.Value,
                levelBox.SelectedItem as string,
                categoryBox.SelectedItem as string);
        }

        cleanupModeBox.SelectionChanged += (_, _) =>
        {
            UpdateCleanupParameterEditor(cleanupModeBox.SelectedIndex, parameterBox, descriptionText, levelBox, categoryBox);
            UpdatePreview();
        };
        parameterBox.ValueChanged += (_, _) => UpdatePreview();
        levelBox.SelectionChanged += (_, _) => UpdatePreview();
        categoryBox.SelectionChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        ThemedContentDialog dialog = new()
        {
            Title = localization.GetString("Logs.Cleanup.Title"),
            Content = content,
            MaxWidth = 720,
            PrimaryButtonText = localization.GetString("Command.Cleanup"),
            CloseButtonText = localization.GetString("Command.Cancel"),
            XamlRoot = XamlRoot,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _viewModel.ApplyCleanupMode(
            cleanupModeBox.SelectedIndex,
            parameterBox.Value,
            levelBox.SelectedItem as string,
            categoryBox.SelectedItem as string);
    }

    /// <summary>Updates the parameter editor to match the selected cleanup mode.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index.</param>
    /// <param name="parameterBox">Numeric parameter editor. Must not be null.</param>
    /// <param name="descriptionText">Cleanup description text. Must not be null.</param>
    private static void UpdateCleanupParameterEditor(
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
                parameterBox.Header = LocalizationService.Instance.GetString("Logs.Cleanup.Parameter.KeepDays");
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 3650;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 30 : Math.Clamp(parameterBox.Value, 1, 3650);
                descriptionText.Text = LocalizationService.Instance.GetString("Logs.Cleanup.Description.ByDate");
                break;
            case 1:
                parameterBox.IsEnabled = true;
                parameterBox.Header = LocalizationService.Instance.GetString("Logs.Cleanup.Parameter.TargetSize");
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 102400;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 10 : Math.Clamp(parameterBox.Value, 1, 102400);
                descriptionText.Text = LocalizationService.Instance.GetString("Logs.Cleanup.Description.BySize");
                break;
            case 2:
                parameterBox.IsEnabled = true;
                parameterBox.Header = LocalizationService.Instance.GetString("Logs.Cleanup.Parameter.KeepLogCount");
                parameterBox.Minimum = 1;
                parameterBox.Maximum = 10000000;
                parameterBox.Value = double.IsNaN(parameterBox.Value) ? 1000 : Math.Clamp(parameterBox.Value, 1, 10000000);
                descriptionText.Text = LocalizationService.Instance.GetString("Logs.Cleanup.Description.ByCount");
                break;
            case 3:
                parameterBox.IsEnabled = false;
                parameterBox.Header = LocalizationService.Instance.GetString("Logs.Cleanup.Parameter.None");
                descriptionText.Text = LocalizationService.Instance.GetString("Logs.Cleanup.Description.All");
                break;
            case 4:
                parameterBox.IsEnabled = false;
                parameterBox.Header = LocalizationService.Instance.GetString("Logs.Cleanup.Parameter.None");
                descriptionText.Text = $"{LocalizationService.Instance.GetString("Logs.Filter.Level")} / {LocalizationService.Instance.GetString("Logs.Filter.Category")}";
                break;
        }
    }

}
