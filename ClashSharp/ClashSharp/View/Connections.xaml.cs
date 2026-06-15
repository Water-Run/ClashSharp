/*
 * Connections Page
 * Provides the active connection monitoring surface
 *
 * @author: WaterRun
 * @file: View/Connections.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for monitoring and managing active network connections.</summary>
/// <remarks>
/// Invariants: Visible text is localized during construction and connection rows are refreshed by explicit user action.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Reads localized strings, calls the local mihomo API, and can persist connection snapshots to SQLite.
/// </remarks>
public sealed partial class Connections : Page
{
    /// <summary>Initializes the connections page and applies localized text.</summary>
    public Connections()
    {
        InitializeComponent();
        RefreshLocalizedText();
        ConnectionStatusText.Text = "尚未刷新连接。";
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;
        PageTitleText.Text = localization.GetString("Nav.Connections");
        DescriptionText.Text = localization.GetString("Page.Connections.Description");
        RefreshConnectionsButton.Label = localization.GetString("Command.Refresh");
        PersistConnectionsButton.Label = localization.GetString("Command.PersistSnapshot");
    }

    /// <summary>Refreshes active connections from the local mihomo API.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void RefreshConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshConnectionsAsync();
    }

    /// <summary>Persists the currently visible active connections to SQLite.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void PersistConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ActiveConnection> connections = await RefreshConnectionsAsync();
        int insertedCount = LogStorageService.Instance.AppendConnectionSnapshot(connections);
        ConnectionStatusText.Text = $"已写入 {insertedCount:N0} 条连接快照。";
        LogStorageService.Instance.AppendLog("Info", "Connections", "Active connection snapshot persisted.", $"{insertedCount:N0} rows.");
    }

    /// <summary>Refreshes active connections and updates the visible list.</summary>
    /// <returns>Active connection rows; empty when the refresh fails.</returns>
    private async Task<IReadOnlyList<ActiveConnection>> RefreshConnectionsAsync()
    {
        try
        {
            IReadOnlyList<ActiveConnection> connections = await MihomoConnectionService.Instance.GetActiveConnectionsAsync(CancellationToken.None);
            ConnectionsList.ItemsSource = connections;
            ConnectionStatusText.Text = $"{connections.Count:N0} 条活动连接。";
            return connections;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            ConnectionsList.ItemsSource = Array.Empty<ActiveConnection>();
            ConnectionStatusText.Text = "无法读取 mihomo 连接。";
            LogStorageService.Instance.AppendLog("Warning", "Connections", "Active connection refresh failed.", exception.Message);
            return [];
        }
    }
}
