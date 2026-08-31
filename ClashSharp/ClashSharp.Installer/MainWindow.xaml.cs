using System.ComponentModel;
using System.Windows;
using ClashSharp.Installer.Presentation;

namespace ClashSharp.Installer;

/// <summary>Hosts the accessible installer shell; behavior remains in the view model.</summary>
public partial class MainWindow : Window
{
    private readonly InstallerShellViewModel _viewModel;
    private bool _initialized;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _viewModelDisposed;

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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_viewModel.IsBusy)
        {
            DisposeViewModel();
            return;
        }

        e.Cancel = true;
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RequestCancellation();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!_closeRequested
            || e.PropertyName != nameof(InstallerShellViewModel.IsBusy)
            || _viewModel.IsBusy)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_closeRequested || _viewModel.IsBusy)
            {
                return;
            }

            _allowClose = true;
            Close();
        });
    }

    private void OnClosed(object? sender, EventArgs e) => DisposeViewModel();

    private void DisposeViewModel()
    {
        if (_viewModelDisposed)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        _viewModelDisposed = true;
    }
}
