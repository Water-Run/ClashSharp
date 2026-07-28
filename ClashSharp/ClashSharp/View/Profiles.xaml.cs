using System;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashSharp.View;

/// <summary>Page for managing subscription configuration profiles.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="ProfilesViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates file pickers and delegates selected file paths to the view model.
/// </remarks>
public sealed partial class Profiles : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly ProfilesViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();

    private readonly Action _reportFilePickerUnavailable;

    /// <summary>Initializes the profiles page and its view model.</summary>
    public Profiles()
        : this(ProfilesPageComposition.Create())
    {
    }

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Profiles(ProfilesPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _reportFilePickerUnavailable = dependencies.ReportFilePickerUnavailable;
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

    /// <summary>Shows a native file picker and imports the selected profile file.</summary>
    /// <param name="sender">Command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(async cancellationToken =>
        {
            if (!TryCreateProfileFilePicker(out FileOpenPicker picker))
            {
                return;
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (file is not null)
            {
                await _viewModel.ImportProfileCommand.ExecuteObservedAsync(
                    file.Path,
                    cancellationToken);
            }
        });
    }

    private async void ValidateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(
            cancellationToken => _viewModel.ValidateProfileCommand.ExecuteObservedAsync(
                null,
                cancellationToken));
    }

    private async void SetActiveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(
            cancellationToken => _viewModel.SetActiveProfileCommand.ExecuteObservedAsync(
                null,
                cancellationToken));
    }

    /// <summary>Creates a native WinUI file picker initialized for the current top-level window.</summary>
    /// <param name="picker">Configured file picker when the main window is available.</param>
    /// <returns>True when the picker is ready; otherwise false.</returns>
    private bool TryCreateProfileFilePicker(out FileOpenPicker picker)
    {
        picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        picker.FileTypeFilter.Add(".txt");

        if (App.MainWindow is null)
        {
            _reportFilePickerUnavailable();
            return false;
        }

        nint hWnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hWnd);
        return true;
    }
}
