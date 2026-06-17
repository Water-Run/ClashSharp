/*
 * Connections Page
 * Hosts active connection monitoring and delegates state to its view model
 *
 * @author: WaterRun
 * @file: View/Connections.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for monitoring and managing active network connections.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="ConnectionsViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates singleton-backed service adapters for the view model.
/// </remarks>
public sealed partial class Connections : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly ConnectionsViewModel _viewModel;

    /// <summary>Initializes the connections page and its view model.</summary>
    public Connections()
    {
        _viewModel = new(
            new ConnectionsLocalizationAdapter(LocalizationService.Instance),
            new ActiveConnectionClientAdapter(MihomoConnectionService.Instance),
            new ConnectionLogAdapter(LogStorageService.Instance));

        InitializeComponent();
        DataContext = _viewModel;
    }
}
