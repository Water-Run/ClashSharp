/*
 * Settings Page
 * Stub page for application settings including language, theme, and proxy core configuration
 *
 * @author: WaterRun
 * @file: View/Settings.xaml.cs
 * @date: 2026-06-15
 */

using ClashSharp.Service;
using ClashSharp.Model;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Stub page for application-wide settings such as language, theme, and proxy core configuration.</summary>
/// <remarks>
/// Invariants: Loaded controls mirror <see cref="AppSettingsService"/> values after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Persists settings when user-facing controls change.
/// </remarks>
public sealed partial class Settings : Page
{
    /// <summary>Suppresses setting writes while persisted values are being loaded into controls.</summary>
    private bool _isLoadingSettings;

    /// <summary>Initializes the settings page and applies localized text.</summary>
    public Settings()
    {
        InitializeComponent();
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.Settings");
        DescriptionText.Text = LocalizationService.Instance.GetString("Page.Settings.Description");
        LoadSettings();
    }

    /// <summary>Loads persisted settings into visible controls.</summary>
    private void LoadSettings()
    {
        _isLoadingSettings = true;
        AppSettingsService settings = AppSettingsService.Instance;
        TransparentProxyToggle.IsOn = settings.TransparentProxyEnabled;
        MixedPortBox.Value = settings.MixedPort;
        CheckStaleProxyToggle.IsOn = settings.CheckStaleProxyOnStartup;
        RestoreProxyOnExitToggle.IsOn = settings.RestoreProxyOnExit;
        ProxyRecoveryModeBox.SelectedIndex = (int)settings.ProxyRecoveryMode;
        MainlandChinaDisplayToggle.IsOn = settings.MainlandChinaDisplayEnabled;
        _isLoadingSettings = false;
    }

    /// <summary>Persists the transparent proxy setting when the switch changes.</summary>
    /// <param name="sender">The transparent proxy switch. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void TransparentProxyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.Instance.TransparentProxyEnabled = TransparentProxyToggle.IsOn;
    }

    /// <summary>Persists the mixed proxy port when the numeric input changes to a valid value.</summary>
    /// <param name="sender">The mixed port numeric input. Not null.</param>
    /// <param name="args">Number box value change arguments. Not null.</param>
    private void MixedPortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoadingSettings || double.IsNaN(args.NewValue))
        {
            return;
        }

        int port = (int)Math.Round(args.NewValue);
        if (port is >= 1 and <= 65535)
        {
            AppSettingsService.Instance.MixedPort = port;
        }
    }

    /// <summary>Persists the startup stale-proxy check setting when the switch changes.</summary>
    /// <param name="sender">The stale proxy check switch. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void CheckStaleProxyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.Instance.CheckStaleProxyOnStartup = CheckStaleProxyToggle.IsOn;
    }

    /// <summary>Persists the shutdown proxy restoration setting when the switch changes.</summary>
    /// <param name="sender">The shutdown proxy restoration switch. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void RestoreProxyOnExitToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.Instance.RestoreProxyOnExit = RestoreProxyOnExitToggle.IsOn;
    }

    /// <summary>Persists the startup proxy recovery mode when the selection changes.</summary>
    /// <param name="sender">The recovery mode selection box. Not null.</param>
    /// <param name="e">Selection change arguments. Not null.</param>
    private void ProxyRecoveryModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || ProxyRecoveryModeBox.SelectedIndex < 0)
        {
            return;
        }

        AppSettingsService.Instance.ProxyRecoveryMode = (ProxyRecoveryMode)ProxyRecoveryModeBox.SelectedIndex;
    }

    /// <summary>Persists the mainland China display setting when the switch changes.</summary>
    /// <param name="sender">The mainland China display switch. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void MainlandChinaDisplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.Instance.MainlandChinaDisplayEnabled = MainlandChinaDisplayToggle.IsOn;
    }
}
