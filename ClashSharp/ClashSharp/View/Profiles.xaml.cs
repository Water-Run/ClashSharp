/*
 * Profiles Page
 * Provides subscription profile management entry points
 *
 * @author: WaterRun
 * @file: View/Profiles.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashSharp.View;

/// <summary>Page for managing subscription configuration profiles.</summary>
/// <remarks>
/// Invariants: Visible text and profile rows are loaded during construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Reads localized strings and profile metadata during construction.
/// </remarks>
public sealed partial class Profiles : Page
{
    /// <summary>Initializes the profiles page and applies localized text.</summary>
    public Profiles()
    {
        InitializeComponent();
        RefreshLocalizedText();
        RefreshProfiles();
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;
        PageTitleText.Text = localization.GetString("Nav.Profiles");
        DescriptionText.Text = localization.GetString("Page.Profiles.Description");
        ImportProfileButton.Label = localization.GetString("Command.Import");
        ValidateProfileButton.Label = localization.GetString("Command.Validate");
        SetActiveProfileButton.Label = localization.GetString("Command.SetActive");
        CurrentProfileTitleText.Text = localization.GetString("Label.CurrentProfile");
    }

    /// <summary>Refreshes profile rows and active-profile status text.</summary>
    private void RefreshProfiles()
    {
        IReadOnlyList<ConfigurationProfile> profiles = ProfileCatalogService.Instance.GetProfiles();
        ProfilesList.ItemsSource = profiles;
        ActiveProfileText.Text = ResolveActiveProfileDisplayText(profiles);
    }

    /// <summary>Handles import-profile command activation.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateProfileFilePicker(out FileOpenPicker picker))
        {
            return;
        }

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            ProfileImportResult result = await ProfileCatalogService.Instance.ImportLocalProfileAsync(file.Path, CancellationToken.None);
            LogStorageService.Instance.AppendLog("Info", "Profiles", $"Local profile imported: {result.ProfileName}.", result.ConfigPath);
            RefreshProfiles();
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or IOException or InvalidOperationException or OperationCanceledException)
        {
            LogStorageService.Instance.AppendLog("Warning", "Profiles", "Local profile import failed.", exception.Message);
        }
    }

    /// <summary>Handles validate-profile command activation.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void ValidateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedProfile(out ConfigurationProfile profile))
        {
            return;
        }

        try
        {
            ProfileImportResult result = await ProfileCatalogService.Instance.ValidateProfileAsync(profile, CancellationToken.None);
            LogStorageService.Instance.AppendLog("Info", "Profiles", $"Profile validation completed: {profile.Name}.", result.ConfigPath);
            RefreshProfiles();
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            LogStorageService.Instance.AppendLog("Warning", "Profiles", "Profile validation failed.", exception.Message);
            RefreshProfiles();
        }
    }

    /// <summary>Sets the selected profile as active when possible.</summary>
    /// <param name="sender">The command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void SetActiveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedProfile(out ConfigurationProfile profile))
        {
            return;
        }

        if (ProfileCatalogService.Instance.TrySetActiveProfile(profile.Id))
        {
            LogStorageService.Instance.AppendLog("Info", "Profiles", $"Active profile changed to {profile.Name}.", profile.Id);
            RefreshProfiles();
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

    /// <summary>Gets the selected profile when one is selected.</summary>
    /// <param name="profile">Selected profile when available.</param>
    /// <returns>True when a profile is selected; otherwise false.</returns>
    private bool TryGetSelectedProfile(out ConfigurationProfile profile)
    {
        if (ProfilesList.SelectedItem is ConfigurationProfile selectedProfile)
        {
            profile = selectedProfile;
            return true;
        }

        profile = default;
        LogStorageService.Instance.AppendLog("Info", "Profiles", "No profile selected.", null);
        return false;
    }

    /// <summary>Resolves the visible active profile summary from the current profile rows.</summary>
    /// <param name="profiles">Current profile rows. Must not be null.</param>
    /// <returns>User-facing active profile summary text; never null.</returns>
    private static string ResolveActiveProfileDisplayText(IEnumerable<ConfigurationProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        foreach (ConfigurationProfile profile in profiles)
        {
            if (profile.IsActive)
            {
                return $"{profile.NameDisplay} - {profile.StatusDisplay}";
            }
        }

        return AppSettingsService.Instance.ActiveProfileId;
    }
}
