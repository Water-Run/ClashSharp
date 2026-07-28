using System;
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

    /// <summary>Initializes the rules page and its view model.</summary>
    public Rules()
        : this(RulesPageComposition.Create())
    {
    }

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Rules(RulesPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
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
}
