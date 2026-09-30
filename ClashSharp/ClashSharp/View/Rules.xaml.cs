using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for rule-provider state, rule hit statistics, and route decisions.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="RulesViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Loads rule state through the explicit page lifecycle.
/// </remarks>
public sealed partial class Rules : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly RulesViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();
    private readonly PageDataChangeSession _dataChanges;

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Rules(RulesPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _dataChanges = new(dependencies.SubscribeToDataChanges, action => DispatcherQueue.TryEnqueue(() => action()),
            [_loadSession.Cancel], ReloadChangedDataAsync, dependencies.ErrorSink, "rules-data-change");
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _dataChanges.Start();
        await _dataChanges.DrainAsync();
        if (!IsLoaded) { return; }
        await _loadSession.RunAsync(_viewModel.ReloadForDataChangeAsync);
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
        await _viewModel.ReloadForDataChangeAsync(cancellationToken);
    }
}
