/*
 * Profiles Page
 * Hosts profile management and delegates profile state to its view model
 *
 * @author: WaterRun
 * @file: View/Profiles.xaml.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Threading;
using ClashSharp.Service;
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

    /// <summary>Initializes the profiles page and its view model.</summary>
    public Profiles()
    {
        _viewModel = new(
            LocalizationService.Instance.GetString,
            ProfileCatalogService.Instance,
            LogStorageService.Instance);

        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Shows a native file picker and imports the selected profile file.</summary>
    /// <param name="sender">Command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateProfileFilePicker(out FileOpenPicker picker))
        {
            return;
        }

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await _viewModel.ImportLocalProfileAsync(file.Path, CancellationToken.None);
        }
    }

    /// <summary>Creates a native WinUI file picker initialized for the current top-level window.</summary>
    /// <param name="picker">Configured file picker when the main window is available.</param>
    /// <returns>True when the picker is ready; otherwise false.</returns>
    private static bool TryCreateProfileFilePicker(out FileOpenPicker picker)
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
            LogStorageService.Instance.AppendLog("Warning", "Profiles", "Profile file picker could not find the main window.", null);
            return false;
        }

        nint hWnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hWnd);
        return true;
    }
}
