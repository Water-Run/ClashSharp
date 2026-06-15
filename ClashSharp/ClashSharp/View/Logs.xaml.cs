/*
 * Logs Page
 * Displays persistent SQLite log storage status and cleanup entry points
 *
 * @author: WaterRun
 * @file: View/Logs.xaml.cs
 * @date: 2026-06-15
 */

using System;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page reserved for SQLite-backed logs, storage usage, and cleanup actions.</summary>
/// <remarks>
/// Invariants: The page reads storage metadata from <see cref="LogStorageService"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Initializes the log database when constructed.
/// </remarks>
public sealed partial class Logs : Page
{
    /// <summary>Initializes the logs page, localized shell text, and storage usage summary.</summary>
    public Logs()
    {
        InitializeComponent();
        RefreshLocalizedText();
        RefreshLogs();
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;
        PageTitleText.Text = localization.GetString("Nav.Logs");
        DescriptionText.Text = localization.GetString("Page.Logs.Description");
        StorageTitleText.Text = localization.GetString("Logs.Storage.Title");
        CleanupButton.Content = localization.GetString("Command.Cleanup");
    }

    /// <summary>Refreshes storage usage and the recent log list from the local SQLite store.</summary>
    private void RefreshLogs()
    {
        LogStorageSummary summary = LogStorageService.Instance.GetStorageSummary();
        StorageUsageText.Text = string.Format(
            LocalizationService.Instance.GetString("Logs.StorageUsage.Format"),
            FormatByteCount(summary.DatabaseSizeBytes),
            summary.LogCount,
            summary.ConnectionCount);
        RecentLogsList.ItemsSource = LogStorageService.Instance.GetRecentLogs(100);
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
        StackPanel content = new()
        {
            Spacing = 12,
        };
        content.Children.Add(cleanupModeBox);
        content.Children.Add(parameterBox);
        content.Children.Add(descriptionText);

        cleanupModeBox.SelectionChanged += (_, _) => UpdateCleanupParameterEditor(cleanupModeBox.SelectedIndex, parameterBox, descriptionText);

        ContentDialog dialog = new()
        {
            Title = localization.GetString("Logs.Cleanup.Title"),
            Content = content,
            PrimaryButtonText = localization.GetString("Command.Cleanup"),
            CloseButtonText = localization.GetString("Command.Cancel"),
            XamlRoot = XamlRoot,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        ApplyCleanupMode(cleanupModeBox.SelectedIndex, parameterBox.Value);
        RefreshLogs();
    }

    /// <summary>Updates the parameter editor to match the selected cleanup mode.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index.</param>
    /// <param name="parameterBox">Numeric parameter editor. Must not be null.</param>
    /// <param name="descriptionText">Cleanup description text. Must not be null.</param>
    private static void UpdateCleanupParameterEditor(int selectedIndex, NumberBox parameterBox, TextBlock descriptionText)
    {
        ArgumentNullException.ThrowIfNull(parameterBox);
        ArgumentNullException.ThrowIfNull(descriptionText);

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
        }
    }

    /// <summary>Applies the selected cleanup mode to the local SQLite log store.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index from the cleanup dialog.</param>
    /// <param name="parameterValue">Numeric cleanup parameter value.</param>
    private static void ApplyCleanupMode(int selectedIndex, double parameterValue)
    {
        LogStorageService service = LogStorageService.Instance;
        switch (selectedIndex)
        {
            case 0:
                int keepDays = CoercePositiveInteger(parameterValue, 30);
                service.CleanupBefore(DateTimeOffset.UtcNow.AddDays(-keepDays));
                break;
            case 1:
                long targetSizeBytes = CoercePositiveInteger(parameterValue, 10) * 1024L * 1024L;
                service.CleanupToSize(targetSizeBytes);
                break;
            case 2:
                service.CleanupToLogCount(CoercePositiveInteger(parameterValue, 1000));
                break;
            case 3:
                service.ClearAll();
                break;
        }
    }

    /// <summary>Converts a NumberBox value to a positive integer with a fallback.</summary>
    /// <param name="value">NumberBox value.</param>
    /// <param name="fallback">Fallback value used for NaN or non-positive values.</param>
    /// <returns>Positive integer cleanup parameter.</returns>
    private static int CoercePositiveInteger(double value, int fallback)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Max(1, (int)Math.Round(value));
    }

    /// <summary>Formats a byte count for compact storage display.</summary>
    /// <param name="bytes">Byte count. Must be non-negative.</param>
    /// <returns>Formatted byte count.</returns>
    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N2} {units[unitIndex]}";
    }
}
