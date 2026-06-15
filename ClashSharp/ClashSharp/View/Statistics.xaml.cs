/*
 * Statistics Page
 * Provides traffic and performance statistics display
 *
 * @author: WaterRun
 * @file: View/Statistics.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for displaying traffic and performance statistics.</summary>
/// <remarks>
/// Invariants: Visible text and SQLite statistics are loaded during construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Reads localized strings and initializes SQLite statistics storage during construction.
/// </remarks>
public sealed partial class Statistics : Page
{
    /// <summary>Initializes the statistics page and applies localized text.</summary>
    public Statistics()
    {
        InitializeComponent();
        RefreshLocalizedText();
        RefreshStatistics();
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;
        PageTitleText.Text = localization.GetString("Nav.Statistics");
        DescriptionText.Text = localization.GetString("Page.Statistics.Description");
        TotalStatisticsTitleText.Text = localization.GetString("Statistics.Total.Title");
        ProfileStatisticsTitleText.Text = localization.GetString("Statistics.Profile.Title");
        NodeStatisticsTitleText.Text = localization.GetString("Statistics.Node.Title");
        ByProfileTitleText.Text = localization.GetString("Statistics.ByProfile.Title");
        ByDateTitleText.Text = localization.GetString("Statistics.ByDate.Title");
        ByNodeTitleText.Text = localization.GetString("Statistics.ByNode.Title");
        LogsShortcutTitleText.Text = localization.GetString("Statistics.LogsShortcut.Title");
        LogsShortcutDescriptionText.Text = localization.GetString("Statistics.LogsShortcut.Description");
        OpenLogsButtonText.Text = localization.GetString("Statistics.OpenLogs");
    }

    /// <summary>Refreshes the visible statistics summary from SQLite.</summary>
    private void RefreshStatistics()
    {
        TrafficStatisticsSummary summary = LogStorageService.Instance.GetTrafficStatisticsSummary();
        LocalizationService localization = LocalizationService.Instance;
        TotalTrafficText.Text = string.Format(
            localization.GetString("Statistics.TotalTraffic.Format"),
            FormatByteCount(summary.TotalUploadBytes),
            FormatByteCount(summary.TotalDownloadBytes));
        ConnectionCountText.Text = string.Format(localization.GetString("Statistics.ConnectionCount.Format"), summary.ConnectionCount);
        ProfileStatisticText.Text = string.Format(localization.GetString("Statistics.ProfileCount.Format"), summary.ProfileCount);
        SnapshotStatisticText.Text = string.Format(localization.GetString("Statistics.SnapshotCount.Format"), summary.SnapshotCount);
        NodeStatisticText.Text = string.Format(localization.GetString("Statistics.NodeCount.Format"), summary.NodeCount, summary.NodeHealthCount);
        RuleStatisticText.Text = string.Format(localization.GetString("Statistics.RuleCount.Format"), summary.RuleCount);
        ProfileTrafficList.ItemsSource = ResolveProfileTrafficRows(LogStorageService.Instance.GetProfileTrafficRows(10));
        DailyTrafficList.ItemsSource = LogStorageService.Instance.GetDailyTrafficRows(14);
        NodeTrafficList.ItemsSource = LogStorageService.Instance.GetNodeTrafficRows(10);
    }

    /// <summary>Navigates from statistics to the log storage page.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(Logs));
    }

    /// <summary>Formats a byte count for compact UI display.</summary>
    /// <param name="bytes">Byte count. Must be non-negative.</param>
    /// <returns>Formatted byte count.</returns>
    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N1} {units[unitIndex]}";
    }

    /// <summary>Replaces stored profile identifiers with current profile display names when possible.</summary>
    /// <param name="rows">Stored profile traffic rows. Must not be null.</param>
    /// <returns>Rows with profile display names applied.</returns>
    private static IReadOnlyList<TrafficStatisticRow> ResolveProfileTrafficRows(IReadOnlyList<TrafficStatisticRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Dictionary<string, string> profileNames = new(StringComparer.Ordinal);
        foreach (ConfigurationProfile profile in ProfileCatalogService.Instance.GetProfiles())
        {
            profileNames[profile.Id] = profile.NameDisplay;
        }

        List<TrafficStatisticRow> resolvedRows = new(rows.Count);
        foreach (TrafficStatisticRow row in rows)
        {
            string label = profileNames.TryGetValue(row.Label, out string? profileName) ? profileName : row.Label;
            resolvedRows.Add(row with { Label = label });
        }

        return resolvedRows;
    }
}
