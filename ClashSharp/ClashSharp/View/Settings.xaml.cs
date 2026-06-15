/*
 * Settings Page
 * Provides application settings for language, proxy behavior, and Windows integration
 *
 * @author: WaterRun
 * @file: View/Settings.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
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
    /// <summary>Command names encoded in diagnostic button tags.</summary>
    private const string DiagnosticActionDiagnose = "Diagnose";

    /// <summary>Command names encoded in diagnostic button tags.</summary>
    private const string DiagnosticActionApply = "Apply";

    /// <summary>Command names encoded in diagnostic button tags.</summary>
    private const string DiagnosticActionReset = "Reset";

    /// <summary>Suppresses setting writes while persisted values are being loaded into controls.</summary>
    private bool _isLoadingSettings;

    /// <summary>Initializes the settings page and applies localized text.</summary>
    public Settings()
    {
        InitializeComponent();
        RefreshLocalizedText();
        LoadSettings();
    }

    /// <summary>Loads persisted settings into visible controls.</summary>
    private void LoadSettings()
    {
        _isLoadingSettings = true;
        AppSettingsService settings = AppSettingsService.Instance;
        LanguageBox.SelectedIndex = (int)settings.DisplayLanguage;
        TransparentProxyToggle.IsOn = settings.TransparentProxyEnabled;
        TunFallbackToggle.IsOn = settings.FallbackToSystemProxyWhenTunFails;
        MixedPortBox.Value = settings.MixedPort;
        ConnectionSamplingToggle.IsOn = settings.ConnectionSamplingEnabled;
        ConnectionSamplingIntervalBox.Value = settings.ConnectionSamplingIntervalSeconds;
        CheckStaleProxyToggle.IsOn = settings.CheckStaleProxyOnStartup;
        RestoreProxyOnExitToggle.IsOn = settings.RestoreProxyOnExit;
        ProxyRecoveryModeBox.SelectedIndex = (int)settings.ProxyRecoveryMode;
        MainlandChinaDisplayToggle.IsOn = settings.MainlandChinaDisplayEnabled;
        _isLoadingSettings = false;
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

        AppLanguage language = (AppLanguage)LanguageBox.SelectedIndex;
        AppSettingsService.Instance.DisplayLanguage = language;
        LocalizationService.Instance.CurrentLanguage = language;
        RefreshLocalizedText();
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;

        PageTitleText.Text = localization.GetString("Nav.Settings");
        DescriptionText.Text = localization.GetString("Page.Settings.Description");
        LanguageSectionTitleText.Text = localization.GetString("Settings.Section.Language");
        LanguageTitleText.Text = localization.GetString("Settings.Language.Title");
        LanguageDescriptionText.Text = localization.GetString("Settings.Language.Description");
        ProxySectionTitleText.Text = localization.GetString("Settings.Section.Proxy");
        TransparentProxyTitleText.Text = localization.GetString("Settings.TransparentProxy.Title");
        TransparentProxyDescriptionText.Text = localization.GetString("Settings.TransparentProxy.Description");
        TunFallbackTitleText.Text = localization.GetString("Settings.TunFallback.Title");
        TunFallbackDescriptionText.Text = localization.GetString("Settings.TunFallback.Description");
        MixedPortTitleText.Text = localization.GetString("Settings.MixedPort.Title");
        MixedPortDescriptionText.Text = localization.GetString("Settings.MixedPort.Description");
        ConnectionSamplingTitleText.Text = localization.GetString("Settings.ConnectionSampling.Title");
        ConnectionSamplingDescriptionText.Text = localization.GetString("Settings.ConnectionSampling.Description");
        SamplingIntervalTitleText.Text = localization.GetString("Settings.SamplingInterval.Title");
        SamplingIntervalDescriptionText.Text = localization.GetString("Settings.SamplingInterval.Description");
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
        CheckStaleProxyTitleText.Text = localization.GetString("Settings.CheckStaleProxy.Title");
        CheckStaleProxyDescriptionText.Text = localization.GetString("Settings.CheckStaleProxy.Description");
        RestoreProxyOnExitTitleText.Text = localization.GetString("Settings.RestoreProxyOnExit.Title");
        RestoreProxyOnExitDescriptionText.Text = localization.GetString("Settings.RestoreProxyOnExit.Description");
        ProxyRecoveryModeTitleText.Text = localization.GetString("Settings.ProxyRecoveryMode.Title");
        ProxyRecoveryModeDescriptionText.Text = localization.GetString("Settings.ProxyRecoveryMode.Description");
        ProxyRecoveryIgnoreItem.Content = localization.GetString("Settings.ProxyRecoveryMode.Ignore");
        ProxyRecoveryEnableItem.Content = localization.GetString("Settings.ProxyRecoveryMode.Enable");
        ProxyRecoveryDisableItem.Content = localization.GetString("Settings.ProxyRecoveryMode.Disable");
        MainlandChinaSectionTitleText.Text = localization.GetString("Settings.Section.MainlandChina");
        MainlandChinaDisplayTitleText.Text = localization.GetString("Settings.MainlandChinaDisplay.Title");
        MainlandChinaDisplayDescriptionText.Text = localization.GetString("Settings.MainlandChinaDisplay.Description");
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

    /// <summary>Persists the automatic TUN fallback setting when the switch changes.</summary>
    /// <param name="sender">The fallback switch. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void TunFallbackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.Instance.FallbackToSystemProxyWhenTunFails = TunFallbackToggle.IsOn;
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

    /// <summary>Persists the background connection sampling setting when the switch changes.</summary>
    /// <param name="sender">The connection sampling switch. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void ConnectionSamplingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.Instance.ConnectionSamplingEnabled = ConnectionSamplingToggle.IsOn;
        ConnectionSamplingService.Instance.RestartFromSettings();
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

        int intervalSeconds = (int)Math.Round(args.NewValue);
        if (intervalSeconds is >= 5 and <= 3600)
        {
            AppSettingsService.Instance.ConnectionSamplingIntervalSeconds = intervalSeconds;
            ConnectionSamplingService.Instance.RestartFromSettings();
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

    /// <summary>Dispatches Windows-native diagnostic buttons by target and action encoded in the button tag.</summary>
    /// <param name="sender">The command button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void WindowsDiagnosticButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadDiagnosticCommand(sender, out WindowsDiagnosticTarget target, out string action))
        {
            LogStorageService.Instance.AppendLog("Warning", "WindowsDiagnostics", "Unsupported diagnostic command.", null);
            return;
        }

        switch (action)
        {
            case DiagnosticActionDiagnose:
                await RunWindowsDiagnosticAsync(target, apply: false);
                break;
            case DiagnosticActionApply:
                await RunWindowsDiagnosticAsync(target, apply: true);
                break;
            case DiagnosticActionReset:
                await ResetWindowsDiagnosticAsync(target);
                break;
            default:
                SetDiagnosticStatus(target, "不支持的操作");
                LogStorageService.Instance.AppendLog("Warning", "WindowsDiagnostics", "Unsupported diagnostic action.", action);
                break;
        }
    }

    /// <summary>Runs or applies one Windows-native network diagnostic and updates the visible status.</summary>
    /// <param name="target">Diagnostic target.</param>
    /// <param name="apply">True to apply repair settings before reporting the target status.</param>
    private async Task RunWindowsDiagnosticAsync(WindowsDiagnosticTarget target, bool apply)
    {
        try
        {
            WindowsDiagnosticResult result = apply
                ? await WindowsNetworkDiagnosticService.Instance.ApplyAsync(target, CancellationToken.None)
                : await WindowsNetworkDiagnosticService.Instance.DiagnoseAsync(target, CancellationToken.None);

            SetDiagnosticStatus(result);
            LogStorageService.Instance.AppendLog("Info", "WindowsDiagnostics", result.Message, result.Detail);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            string message = apply ? "应用失败" : "诊断失败";
            SetDiagnosticStatus(target, message);
            LogStorageService.Instance.AppendLog("Warning", "WindowsDiagnostics", message, exception.Message);
        }
    }

    /// <summary>Resets one Windows-native network diagnostic target and updates the visible status.</summary>
    /// <param name="target">Diagnostic target.</param>
    private async Task ResetWindowsDiagnosticAsync(WindowsDiagnosticTarget target)
    {
        try
        {
            WindowsDiagnosticResult result = await WindowsNetworkDiagnosticService.Instance.ResetAsync(target, CancellationToken.None);
            SetDiagnosticStatus(result);
            LogStorageService.Instance.AppendLog("Info", "WindowsDiagnostics", result.Message, result.Detail);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            string message = "还原失败";
            SetDiagnosticStatus(target, message);
            LogStorageService.Instance.AppendLog("Warning", "WindowsDiagnostics", message, exception.Message);
        }
    }

    /// <summary>Updates the visible status text for one diagnostic result.</summary>
    /// <param name="result">Diagnostic result to display.</param>
    private void SetDiagnosticStatus(WindowsDiagnosticResult result)
    {
        SetDiagnosticStatus(result.Target, result.Message);
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

    /// <summary>Reads the diagnostic target and action from a button tag.</summary>
    /// <param name="sender">Event sender expected to be a button. May be null.</param>
    /// <param name="target">Parsed diagnostic target.</param>
    /// <param name="action">Parsed diagnostic action; never null on success.</param>
    /// <returns>True when the sender tag contains a supported target and action.</returns>
    private static bool TryReadDiagnosticCommand(object sender, out WindowsDiagnosticTarget target, out string action)
    {
        target = default;
        action = string.Empty;

        if (sender is not Button { Tag: string tag })
        {
            return false;
        }

        string[] parts = tag.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        action = parts[1];
        return TryParseDiagnosticTarget(parts[0], out target)
            && (StringComparer.Ordinal.Equals(action, DiagnosticActionDiagnose)
                || StringComparer.Ordinal.Equals(action, DiagnosticActionApply)
                || StringComparer.Ordinal.Equals(action, DiagnosticActionReset));
    }

    /// <summary>Parses a diagnostic target name from XAML button metadata.</summary>
    /// <param name="value">Target name. Must not be null.</param>
    /// <param name="target">Parsed diagnostic target.</param>
    /// <returns>True when <paramref name="value"/> maps to a known diagnostic target.</returns>
    private static bool TryParseDiagnosticTarget(string value, out WindowsDiagnosticTarget target)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (value)
        {
            case nameof(WindowsDiagnosticTarget.Wsl):
                target = WindowsDiagnosticTarget.Wsl;
                return true;
            case nameof(WindowsDiagnosticTarget.Terminal):
                target = WindowsDiagnosticTarget.Terminal;
                return true;
            case nameof(WindowsDiagnosticTarget.MicrosoftStore):
                target = WindowsDiagnosticTarget.MicrosoftStore;
                return true;
            default:
                target = WindowsDiagnosticTarget.Wsl;
                return false;
        }
    }
}
