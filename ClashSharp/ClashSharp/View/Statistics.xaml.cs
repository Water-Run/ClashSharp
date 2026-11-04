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

    /// <summary>Reflows the ranked breakdown cards when their content area is too narrow for three readable columns.</summary>
    private void StatisticsCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid panel || e.NewSize.Width <= 0) { return; }
        ReflowCards(panel, e.NewSize.Width < 820 ? 1 : 3);
    }

    /// <summary>Keeps the four headline stat tiles in one row, two rows of two, or one column as width allows.</summary>
    private void SummaryCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid panel || e.NewSize.Width <= 0) { return; }
        int columns = e.NewSize.Width switch
        {
            >= 760 => 4,
            >= 380 => 2,
            _ => 1,
        };
        ReflowCards(panel, columns);
    }

    /// <summary>Places a grid's cards row by row in <paramref name="columns"/> equal star columns.</summary>
    /// <param name="panel">Card grid to rebuild. Not null.</param>
    /// <param name="columns">Visible column count; at least one.</param>
    /// <remarks>Column definitions are rebuilt rather than collapsed because Grid spacing also applies to empty columns.</remarks>
    private static void ReflowCards(Grid panel, int columns)
    {
        columns = Math.Max(1, columns);
        int rows = (panel.Children.Count + columns - 1) / columns;
        if (panel.ColumnDefinitions.Count == columns && panel.RowDefinitions.Count == rows)
        {
            return;
        }

        panel.ColumnDefinitions.Clear();
        for (int column = 0; column < columns; column++)
        {
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        panel.RowDefinitions.Clear();
        for (int row = 0; row < rows; row++)
        {
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        panel.ColumnSpacing = columns > 1 ? 12 : 0;
        panel.RowSpacing = rows > 1 ? 12 : 0;
        for (int index = 0; index < panel.Children.Count; index++)
        {
            FrameworkElement card = (FrameworkElement)panel.Children[index];
            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
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
