/*
 * Statistics Page
 * Hosts the traffic statistics view and delegates statistics state to its view model
 *
 * @author: WaterRun
 * @file: View/Statistics.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for displaying traffic and performance statistics.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="StatisticsViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates singleton-backed service adapters for the view model and navigates to logs on command.
/// </remarks>
public sealed partial class Statistics : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly StatisticsViewModel _viewModel;

    /// <summary>Initializes the statistics page and its view model.</summary>
    public Statistics()
    {
        _viewModel = new(
            new DisplayPageLocalizationAdapter(LocalizationService.Instance),
            new StatisticsStoreAdapter(LogStorageService.Instance),
            new StatisticsProfilesAdapter(ProfileCatalogService.Instance),
            () => Frame.Navigate(typeof(Logs)));

        InitializeComponent();
        DataContext = _viewModel;
    }
}
