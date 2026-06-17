/*
 * Master Control Page
 * Hosts the master control view and delegates runtime state to its view model
 *
 * @author: WaterRun
 * @file: View/MasterControl.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for the master control panel displaying proxy status overview and primary takeover state actions.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="MasterControlViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates singleton-backed service adapters for the view model and starts status loading on page load.
/// </remarks>
public sealed partial class MasterControl : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly MasterControlViewModel _viewModel;

    /// <summary>Initializes the master control page and its view model.</summary>
    public MasterControl()
    {
        _viewModel = new(
            new MasterControlLocalizationAdapter(LocalizationService.Instance),
            new MasterControlCoreAdapter(MihomoCoreService.Instance),
            new MasterControlWindowsProxyAdapter(WindowsProxyService.Instance),
            new MasterControlSettingsAdapter(AppSettingsService.Instance),
            new MasterControlTakeoverAdapter(NetworkTakeoverService.Instance),
            new MasterControlLogAdapter(LogStorageService.Instance));

        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>Starts runtime status loading when the page is loaded.</summary>
    /// <param name="sender">Loaded page instance. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.LoadCommand.Execute(null);
    }
}
