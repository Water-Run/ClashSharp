/*
 * Proxies Page
 * Provides proxy group, node, and region preview management
 *
 * @author: WaterRun
 * @file: View/Proxies.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Threading;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for managing proxy groups and individual proxy nodes.</summary>
/// <remarks>
/// Invariants: Visible text and active profile nodes are loaded during construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Reads localized strings and active profile node metadata during construction.
/// </remarks>
public sealed partial class Proxies : Page
{
    /// <summary>Initializes the proxies page and applies localized text.</summary>
    public Proxies()
    {
        InitializeComponent();
        RefreshLocalizedText();
        RefreshNodes();
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;
        PageTitleText.Text = localization.GetString("Nav.ProxyNodes");
        DescriptionText.Text = localization.GetString("Page.ProxyNodes.Description");
        RefreshNodesButton.Label = localization.GetString("Command.Refresh");
        TestLatencyButton.Label = localization.GetString("Command.TestLatency");
    }

    /// <summary>Refreshes visible proxy nodes from the active profile.</summary>
    private void RefreshNodes()
    {
        ProxyNodesList.ItemsSource = ProxyNodeCatalogService.Instance.GetNodes();
    }

    /// <summary>Handles refresh command activation.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void RefreshNodesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshNodes();
    }

    /// <summary>Tests latency for all visible proxy nodes.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void TestLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProxyNodesList.ItemsSource is not IReadOnlyList<ProxyNode> nodes)
        {
            nodes = ProxyNodeCatalogService.Instance.GetNodes();
        }

        try
        {
            IReadOnlyList<ProxyNode> testedNodes = await ProxyLatencyService.Instance.TestNodesAsync(nodes, CancellationToken.None);
            ProxyNodesList.ItemsSource = testedNodes;
            LogStorageService.Instance.AppendLog("Info", "ProxyNodes", "Proxy node latency test completed.", $"{testedNodes.Count:N0} nodes tested.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            LogStorageService.Instance.AppendLog("Warning", "ProxyNodes", "Proxy node latency test failed.", exception.Message);
        }
    }
}
