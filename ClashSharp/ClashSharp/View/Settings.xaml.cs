/*
 * Settings Page
 * Provides application settings for language, proxy behavior, and Windows integration
 *
 * @author: WaterRun
 * @file: View/Settings.xaml.cs
 * @date: 2026-06-17
 */

using System;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for application-wide settings such as language, Windows proxy behavior, and core configuration.</summary>
/// <remarks>
/// Invariants: Loaded controls mirror <see cref="AppSettingsService"/> values after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Persists settings when user-facing controls change.
/// </remarks>
public sealed partial class Settings : Page
{
    /// <summary>Owns settings state transitions and persistence.</summary>
    private readonly SettingsViewModel _viewModel;

    /// <summary>Initializes the settings page and applies localized text.</summary>
    public Settings()
    {
        SettingsDiagnosticsViewModel diagnosticsViewModel = new(
            new WindowsDiagnosticsClient(WindowsNetworkDiagnosticService.Instance),
            new DiagnosticsLog(LogStorageService.Instance));
        _viewModel = new(
            new AppSettingsStore(AppSettingsService.Instance),
            language => LocalizationService.Instance.CurrentLanguage = language,
            ConnectionSamplingService.Instance.RestartFromSettings,
            LocalizationService.Instance.GetString,
            SettingsProxyInformationAdapter.CreateSnapshot,
            diagnosticsViewModel);
        InitializeComponent();
        DataContext = _viewModel;
        LoadSettings();
    }

    /// <summary>Loads persisted settings into visible controls.</summary>
    private void LoadSettings()
    {
        _viewModel.Load();
    }

}
