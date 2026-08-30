using System.ComponentModel;
using System.Windows;
using ClashSharp.Installer.Presentation;

namespace ClashSharp.Installer;

/// <summary>Hosts the accessible installer shell; behavior remains in the view model.</summary>
public partial class MainWindow : Window
{
    private readonly InstallerShellViewModel _viewModel;
    private bool _initialized;

    /// <summary>Initializes the window from its explicit presentation model.</summary>
    public MainWindow(InstallerShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.Dispose();
}
