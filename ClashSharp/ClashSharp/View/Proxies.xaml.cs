/*
 * Proxies Page
 * Hosts the proxy node view and delegates proxy state to its view model
 *
 * @author: WaterRun
 * @file: View/Proxies.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using ClashSharp.Service;
using ClashSharp.ViewModel;
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
            new ProxiesLogAdapter(LogStorageService.Instance));

        InitializeComponent();
        DataContext = _viewModel;
    }
}
