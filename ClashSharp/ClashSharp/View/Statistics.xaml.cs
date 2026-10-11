using System;
using System.Threading;
using System.Threading.Tasks;
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
/// Side effects: Requests semantic logs navigation on command.
/// </remarks>
public sealed partial class Statistics : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly StatisticsViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();
    private readonly PageDataChangeSession _dataChanges;

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Statistics(StatisticsPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _dataChanges = new(dependencies.SubscribeToDataChanges, action => DispatcherQueue.TryEnqueue(() => action()),
            [_loadSession.Cancel], ReloadChangedDataAsync, dependencies.ErrorSink, "statistics-data-change");
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Reflows summary and breakdown cards when their content area is too narrow for three readable columns.</summary>
    private void StatisticsCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid panel || e.NewSize.Width <= 0) { return; }
        bool compact = e.NewSize.Width < 820;
        if ((panel.ColumnDefinitions[1].Width.Value == 0) == compact) { return; }
        panel.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        panel.ColumnDefinitions[2].Width = panel.ColumnDefinitions[1].Width;
        panel.ColumnSpacing = compact ? 0 : 12;
        panel.RowSpacing = compact ? 12 : 0;
        panel.RowDefinitions.Clear();
        for (int row = 0; row < (compact ? 3 : 1); row++)
        {
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (int index = 0; index < panel.Children.Count; index++)
        {
            FrameworkElement card = (FrameworkElement)panel.Children[index];
            Grid.SetRow(card, compact ? index : 0);
            Grid.SetColumn(card, compact ? 0 : index);
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _dataChanges.Start();
        await _dataChanges.DrainAsync();
        if (!IsLoaded) { return; }
        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    private async void RefreshStatisticsButton_Click(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _dataChanges.Stop();
        _loadSession.Cancel();
    }

    private async Task ReloadChangedDataAsync(CancellationToken cancellationToken)
    {
        await _loadSession.DrainAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await _loadSession.RunAsync(_viewModel.ReloadForDataChangeAsync, cancellationToken: cancellationToken);
    }
}
