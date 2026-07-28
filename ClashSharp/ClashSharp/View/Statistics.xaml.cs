using System;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for displaying traffic and performance statistics.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="StatisticsViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Navigates its owning frame to logs on command.
/// </remarks>
public sealed partial class Statistics : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly StatisticsViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();

    /// <summary>Initializes the statistics page and its view model.</summary>
    public Statistics()
        : this(StatisticsPageComposition.Create())
    {
    }

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Statistics(StatisticsPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.CreateViewModel(OpenLogs);
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadSession.Cancel();
    }

    /// <summary>Navigates to the logs page inside the owning frame.</summary>
    private void OpenLogs()
    {
        Frame.Navigate(typeof(Logs));
    }
}
