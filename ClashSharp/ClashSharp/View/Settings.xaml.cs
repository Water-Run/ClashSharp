/*
 * Settings Page
 * Provides application settings for language, proxy behavior, and Windows integration
 *
 * @author: WaterRun
 * @file: View/Settings.xaml.cs
 * @date: 2026-06-17
 */

using System;
using System.Threading;
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

    /// <summary>Owns Windows-native diagnostics command routing.</summary>
    private readonly SettingsDiagnosticsViewModel _diagnosticsViewModel;

    /// <summary>Suppresses setting writes while persisted values are being loaded into controls.</summary>
    private bool _isLoadingSettings;

    /// <summary>Initializes the settings page and applies localized text.</summary>
    public Settings()
    {
        _viewModel = new(
            new AppSettingsStore(AppSettingsService.Instance),
            language => LocalizationService.Instance.CurrentLanguage = language,
            ConnectionSamplingService.Instance.RestartFromSettings);
        _diagnosticsViewModel = new(
            new WindowsDiagnosticsClient(WindowsNetworkDiagnosticService.Instance),
            new DiagnosticsLog(LogStorageService.Instance));
        InitializeComponent();
        RefreshLocalizedText();
        LoadSettings();
    }

    /// <summary>Loads persisted settings into visible controls.</summary>
    private void LoadSettings()
    {
        _isLoadingSettings = true;
        _viewModel.Load();
        LanguageBox.SelectedIndex = _viewModel.DisplayLanguageIndex;
        TransparentProxyToggle.IsOn = _viewModel.TransparentProxyEnabled;
        TunFallbackToggle.IsOn = _viewModel.FallbackToSystemProxyWhenTunFails;
        MixedPortBox.Value = _viewModel.MixedPort;
        ConnectionSamplingToggle.IsOn = _viewModel.ConnectionSamplingEnabled;
        ConnectionSamplingIntervalBox.Value = _viewModel.ConnectionSamplingIntervalSeconds;
        CheckStaleProxyToggle.IsOn = _viewModel.CheckStaleProxyOnStartup;
        RestoreProxyOnExitToggle.IsOn = _viewModel.RestoreProxyOnExit;
        ProxyRecoveryModeBox.SelectedIndex = _viewModel.ProxyRecoveryModeIndex;
        MainlandChinaFeatureModeBox.SelectedIndex = _viewModel.MainlandChinaFeatureModeIndex;
        _isLoadingSettings = false;
        RefreshProxyInformation();
    }

    /// <summary>Persists the display language when the selection changes and notifies the application shell.</summary>
    /// <param name="sender">The language selection box. Not null.</param>
    /// <param name="e">Selection change arguments. Not null.</param>
    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || LanguageBox.SelectedIndex < 0)
        {
            return;
        }

        if (_viewModel.SetDisplayLanguageIndex(LanguageBox.SelectedIndex))
        {
            RefreshLocalizedText();
        }
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;

        PageTitleText.Text = localization.GetString("Nav.Settings");
        DescriptionText.Text = localization.GetString("Page.Settings.Description");
        LanguageSectionTitleText.Text = localization.GetString("Settings.Section.Language");
        LanguageRow.Title = localization.GetString("Settings.Language.Title");
        LanguageRow.Description = localization.GetString("Settings.Language.Description");
        ProxySectionTitleText.Text = localization.GetString("Settings.Section.Proxy");
        TransparentProxyRow.Title = localization.GetString("Settings.TransparentProxy.Title");
        TransparentProxyRow.Description = localization.GetString("Settings.TransparentProxy.Description");
        TunFallbackRow.Title = localization.GetString("Settings.TunFallback.Title");
        TunFallbackRow.Description = localization.GetString("Settings.TunFallback.Description");
        MixedPortRow.Title = localization.GetString("Settings.MixedPort.Title");
        MixedPortRow.Description = localization.GetString("Settings.MixedPort.Description");
        ProxyInformationTitleText.Text = localization.GetString("Settings.ProxyInformation.Title");
        ProxyInformationDescriptionText.Text = localization.GetString("Settings.ProxyInformation.Description");
        ConnectionSamplingRow.Title = localization.GetString("Settings.ConnectionSampling.Title");
        ConnectionSamplingRow.Description = localization.GetString("Settings.ConnectionSampling.Description");
        SamplingIntervalRow.Title = localization.GetString("Settings.SamplingInterval.Title");
        SamplingIntervalRow.Description = localization.GetString("Settings.SamplingInterval.Description");
        WindowsNativeSectionTitleText.Text = localization.GetString("Settings.Section.WindowsNative");
        WindowsNativeTitleText.Text = localization.GetString("Settings.WindowsNative.Title");
        WindowsNativeDescriptionText.Text = localization.GetString("Settings.WindowsNative.Description");
        WslDiagnosticTitleText.Text = localization.GetString("Settings.Wsl.Title");
        TerminalDiagnosticTitleText.Text = localization.GetString("Settings.Terminal.Title");
        StoreDiagnosticTitleText.Text = localization.GetString("Settings.Store.Title");
        DiagnoseWslText.Text = localization.GetString("Command.Diagnose");
        DiagnoseTerminalText.Text = localization.GetString("Command.Diagnose");
        DiagnoseStoreText.Text = localization.GetString("Command.Diagnose");
        ApplyWslText.Text = localization.GetString("Command.Apply");
        ApplyTerminalText.Text = localization.GetString("Command.Apply");
        ApplyStoreText.Text = localization.GetString("Command.Apply");
        ResetWslText.Text = localization.GetString("Command.Reset");
        ResetTerminalText.Text = localization.GetString("Command.Reset");
        ResetStoreText.Text = localization.GetString("Command.Reset");
        WslDiagnosticStatusText.Text = localization.GetString("Diagnostic.NotRun");
        TerminalDiagnosticStatusText.Text = localization.GetString("Diagnostic.NotRun");
        StoreDiagnosticStatusText.Text = localization.GetString("Diagnostic.NotRun");
        CheckStaleProxyRow.Title = localization.GetString("Settings.CheckStaleProxy.Title");
        CheckStaleProxyRow.Description = localization.GetString("Settings.CheckStaleProxy.Description");
        RestoreProxyOnExitRow.Title = localization.GetString("Settings.RestoreProxyOnExit.Title");
        RestoreProxyOnExitRow.Description = localization.GetString("Settings.RestoreProxyOnExit.Description");
        ProxyRecoveryModeRow.Title = localization.GetString("Settings.ProxyRecoveryMode.Title");
        ProxyRecoveryModeRow.Description = localization.GetString("Settings.ProxyRecoveryMode.Description");
        ProxyRecoveryIgnoreItem.Content = localization.GetString("Settings.ProxyRecoveryMode.Ignore");
        ProxyRecoveryEnableItem.Content = localization.GetString("Settings.ProxyRecoveryMode.Enable");
        ProxyRecoveryDisableItem.Content = localization.GetString("Settings.ProxyRecoveryMode.Disable");
        MainlandChinaSectionTitleText.Text = localization.GetString("Settings.Section.MainlandChina");
        MainlandChinaDisplayRow.Title = localization.GetString("Settings.MainlandChinaDisplay.Title");
        MainlandChinaDisplayRow.Description = localization.GetString("Settings.MainlandChinaDisplay.Description");
        MainlandChinaDisabledItem.Content = localization.GetString("Settings.MainlandChinaFeature.Disabled");
        MainlandChinaFlagOnlyItem.Content = localization.GetString("Settings.MainlandChinaFeature.FlagOnly");
        MainlandChinaFlagAndTextItem.Content = localization.GetString("Settings.MainlandChinaFeature.FlagAndText");
        MainlandChinaKeywordFilterItem.Content = localization.GetString("Settings.MainlandChinaFeature.KeywordFilter");
        MainlandChinaAllItem.Content = localization.GetString("Settings.MainlandChinaFeature.All");
        RefreshProxyInformation();
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

        _viewModel.SetTransparentProxyEnabled(TransparentProxyToggle.IsOn);
    }

    /// <summary>Persists the automatic TUN fallback setting when the switch changes.</summary>
    /// <param name="sender">The fallback switch. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void TunFallbackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _viewModel.SetFallbackToSystemProxyWhenTunFails(TunFallbackToggle.IsOn);
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

        if (_viewModel.SetMixedPort(args.NewValue))
        {
            RefreshProxyInformation();
        }
    }

    /// <summary>Persists the background connection sampling setting when the switch changes.</summary>
    /// <param name="sender">The connection sampling switch. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void ConnectionSamplingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _viewModel.SetConnectionSamplingEnabled(ConnectionSamplingToggle.IsOn);
    }

    /// <summary>Persists the background connection sampling interval when the numeric input changes to a valid value.</summary>
    /// <param name="sender">The sampling interval numeric input. Not null.</param>
    /// <param name="args">Number box value change arguments. Not null.</param>
    private void ConnectionSamplingIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoadingSettings || double.IsNaN(args.NewValue))
        {
            return;
        }

        _viewModel.SetConnectionSamplingIntervalSeconds(args.NewValue);
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

        _viewModel.SetCheckStaleProxyOnStartup(CheckStaleProxyToggle.IsOn);
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

        _viewModel.SetRestoreProxyOnExit(RestoreProxyOnExitToggle.IsOn);
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

        _viewModel.SetProxyRecoveryModeIndex(ProxyRecoveryModeBox.SelectedIndex);
    }

    /// <summary>Persists the mainland China feature mode when the selection changes.</summary>
    /// <param name="sender">The mainland China mode selector. Not null.</param>
    /// <param name="e">Selection change arguments. Not null.</param>
    private void MainlandChinaFeatureModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || MainlandChinaFeatureModeBox.SelectedIndex < 0)
        {
            return;
        }

        _viewModel.SetMainlandChinaFeatureModeIndex(MainlandChinaFeatureModeBox.SelectedIndex);
    }

    /// <summary>Refreshes immutable proxy and core path information shown in the proxy settings section.</summary>
    private void RefreshProxyInformation()
    {
        LocalizationService localization = LocalizationService.Instance;
        CoreConfigurationState configurationState = CoreConfigurationService.Instance.GetState();
        string coreBinaryText = MihomoCoreService.Instance.IsBinaryAvailable
            ? MihomoCoreService.Instance.BinaryPath
            : localization.GetString("Settings.ProxyInformation.CoreBinary.Missing");

        ProxyLocalEntryText.Text = string.Format(
            localization.GetString("Settings.ProxyInformation.LocalEntry.Format"),
            _viewModel.MixedPort);
        ProxyCoreConfigurationText.Text = string.Format(
            localization.GetString("Settings.ProxyInformation.CoreConfig.Format"),
            configurationState.ConfigPath);
        ProxyCoreBinaryText.Text = string.Format(
            localization.GetString("Settings.ProxyInformation.CoreBinary.Format"),
            coreBinaryText);
    }

    /// <summary>Dispatches Windows-native diagnostic buttons by target and action encoded in the button tag.</summary>
    /// <param name="sender">The command button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void WindowsDiagnosticButton_Click(object sender, RoutedEventArgs e)
    {
        string? commandTag = sender is Button { Tag: string tag } ? tag : null;
        SettingsDiagnosticStatus? status = await _diagnosticsViewModel.ExecuteCommandAsync(commandTag, CancellationToken.None);
        if (status is SettingsDiagnosticStatus value)
        {
            SetDiagnosticStatus(value.Target, value.Message);
        }
    }

    /// <summary>Updates the visible status text for one diagnostic target.</summary>
    /// <param name="target">Diagnostic target.</param>
    /// <param name="message">Status message. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    private void SetDiagnosticStatus(WindowsDiagnosticTarget target, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (target)
        {
            case WindowsDiagnosticTarget.Wsl:
                WslDiagnosticStatusText.Text = message;
                break;
            case WindowsDiagnosticTarget.Terminal:
                TerminalDiagnosticStatusText.Text = message;
                break;
            case WindowsDiagnosticTarget.MicrosoftStore:
                StoreDiagnosticStatusText.Text = message;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported Windows diagnostic target.");
        }
    }

}
