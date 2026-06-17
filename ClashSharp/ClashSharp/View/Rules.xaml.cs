/*
 * Rules Page
 * Hosts the rule inspection view and delegates rule state to its view model
 *
 * @author: WaterRun
 * @file: View/Rules.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for rule-provider state, rule hit statistics, and route decisions.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="RulesViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates singleton-backed service adapters for the view model.
/// </remarks>
public sealed partial class Rules : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly RulesViewModel _viewModel;

    /// <summary>Initializes the rules page and its view model.</summary>
    public Rules()
    {
        _viewModel = new(
            new DisplayPageLocalizationAdapter(LocalizationService.Instance),
            new RuleCatalogAdapter(RuleCatalogService.Instance));

        InitializeComponent();
        DataContext = _viewModel;
    }
}
