/*
 * Proxies Page
 * Hosts the proxy node view and delegates proxy state to its view model
 *
 * @author: WaterRun
 * @file: View/Proxies.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for managing proxy groups and individual proxy nodes.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="ProxiesViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates singleton-backed service adapters for the view model.
/// </remarks>
public sealed partial class Proxies : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly ProxiesViewModel _viewModel;

    /// <summary>Initializes the proxies page and its view model.</summary>
    public Proxies()
    {
        _viewModel = new(
            new ProxiesLocalizationAdapter(LocalizationService.Instance),
            new ProxyNodeCatalogAdapter(ProxyNodeCatalogService.Instance),
            new ProxyLatencyTesterAdapter(ProxyLatencyService.Instance),
            new ProxyRuntimeControllerAdapter(MihomoControllerClient.Instance),
            new ProxiesLogAdapter(LogStorageService.Instance));

        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>Refreshes mihomo runtime state when the page first loads.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.RefreshRuntimeAsync(default);
    }

    /// <summary>Handles runtime strategy group selection changes.</summary>
    private async void ProxyGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: MihomoProxyGroup group, SelectedItem: string proxyName }
            || string.Equals(group.CurrentSelection, proxyName, StringComparison.Ordinal))
        {
            return;
        }

        await _viewModel.SelectProxyAsync(group, proxyName, default);
    }

}
