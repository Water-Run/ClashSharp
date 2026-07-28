using System;
using ClashSharp.Presentation.Composition;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for monitoring and managing active network connections.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="ConnectionsViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: None beyond XAML initialization and data binding.
/// </remarks>
public sealed partial class Connections : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly ConnectionsViewModel _viewModel;

    /// <summary>Initializes the connections page and its view model.</summary>
    public Connections()
        : this(ConnectionsPageComposition.Create())
    {
    }

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Connections(ConnectionsPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        InitializeComponent();
        DataContext = _viewModel;
    }
}
