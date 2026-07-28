using System;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for managing proxy groups and individual proxy nodes.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="ProxiesViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Refreshes runtime state when loaded and delegates user selections to the view model.
/// </remarks>
public sealed partial class Proxies : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly ProxiesViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();

    private readonly PageLoadSession _selectionSession = new();

    /// <summary>Initializes the proxies page and its view model.</summary>
    public Proxies()
        : this(ProxiesPageComposition.Create())
    {
    }

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Proxies(ProxiesPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Loads catalog and mihomo runtime state while the page is active.</summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    /// <summary>Cancels page-owned requests before the visual tree is released.</summary>
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadSession.Cancel();
        _selectionSession.Cancel();
    }

    /// <summary>Handles runtime strategy group selection changes.</summary>
    private async void ProxyGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: MihomoProxyGroupDisplay group, SelectedItem: string proxyName }
            || string.Equals(group.CurrentSelection, proxyName, StringComparison.Ordinal))
        {
            return;
        }

        await _selectionSession.RunAsync(
            cancellationToken => _viewModel.SelectProxyAsync(group.Model, proxyName, cancellationToken));
    }
}
