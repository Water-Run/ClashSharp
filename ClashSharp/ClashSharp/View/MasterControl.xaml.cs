/*
 * Master Control Page
 * Dashboard page serving as the main status controller with proxy status overview and quick actions
 *
 * @author: WaterRun
 * @file: View/MasterControl.xaml.cs
 * @date: 2026-06-15
 */

using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for the master control panel displaying proxy status overview and primary takeover state actions.</summary>
/// <remarks>
/// Invariants: Exactly one primary takeover mode button is selected at any time.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Updates local view state when a takeover mode button is clicked.
/// </remarks>
public sealed partial class MasterControl : Page
{
    /// <summary>Currently selected takeover mode for the master status control.</summary>
    private ClashSharpMode _selectedMode = AppSettingsService.Instance.CurrentMode;

    /// <summary>Initializes the master control page and applies localized text.</summary>
    public MasterControl()
    {
        InitializeComponent();
        RefreshLocalizedText();
        UpdateModeButtons();
        Loaded += OnLoaded;
    }

    /// <summary>Refreshes localized text owned by this page.</summary>
    private void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Instance;

        PageTitleText.Text = localization.GetString("Nav.MasterControl");
        DescriptionText.Text = localization.GetString("Page.MasterControl.Description");
        StatusControlTitleText.Text = localization.GetString("Master.StatusControl.Title");
        StatusControlDescriptionText.Text = localization.GetString("Master.StatusControl.Description");
        DisabledModeTitleText.Text = localization.GetString("Master.Mode.Disabled.Title");
        DisabledModeDescriptionText.Text = localization.GetString("Master.Mode.Disabled.Description");
        StandbyModeTitleText.Text = localization.GetString("Master.Mode.Standby.Title");
        StandbyModeDescriptionText.Text = localization.GetString("Master.Mode.Standby.Description");
        RuleTakeoverModeTitleText.Text = localization.GetString("Master.Mode.RuleTakeover.Title");
        RuleTakeoverModeDescriptionText.Text = localization.GetString("Master.Mode.RuleTakeover.Description");
        FullTakeoverModeTitleText.Text = localization.GetString("Master.Mode.FullTakeover.Title");
        FullTakeoverModeDescriptionText.Text = localization.GetString("Master.Mode.FullTakeover.Description");
        CoreStatusTitleText.Text = localization.GetString("Master.Status.Core");
        SystemProxyTitleText.Text = localization.GetString("Master.Status.SystemProxy");
        TransparentProxyTitleText.Text = localization.GetString("Master.Status.TransparentProxy");
    }

    /// <summary>Handles page loading by probing the bundled mihomo core version for the status summary.</summary>
    /// <param name="sender">The loaded page instance. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string versionText = await MihomoCoreService.Instance.GetVersionTextAsync(CancellationToken.None);
            CoreStatusText.Text = string.Format(
                LocalizationService.Instance.GetString("Master.Status.CoreReady.Format"),
                versionText);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException)
        {
            CoreStatusText.Text = LocalizationService.Instance.GetString("Master.Status.CoreUnavailable");
        }

        RefreshProxyStatus();
    }

    /// <summary>Selects the disabled takeover mode from the primary status control.</summary>
    /// <param name="sender">The clicked mode button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void DisabledModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMode(ClashSharpMode.Disabled);
    }

    /// <summary>Selects the standby takeover mode from the primary status control.</summary>
    /// <param name="sender">The clicked mode button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void StandbyModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMode(ClashSharpMode.Standby);
    }

    /// <summary>Selects the rule takeover mode from the primary status control.</summary>
    /// <param name="sender">The clicked mode button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void RuleTakeoverModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMode(ClashSharpMode.RuleTakeover);
    }

    /// <summary>Selects the full takeover mode from the primary status control.</summary>
    /// <param name="sender">The clicked mode button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void FullTakeoverModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMode(ClashSharpMode.FullTakeover);
    }

    /// <summary>Updates the selected takeover mode and refreshes the primary status buttons.</summary>
    /// <param name="mode">The newly selected <see cref="ClashSharpMode"/> value.</param>
    private void SetSelectedMode(ClashSharpMode mode)
    {
        try
        {
            NetworkTakeoverResult result = NetworkTakeoverService.Instance.ApplyMode(mode);
            _selectedMode = mode;
            AppSettingsService.Instance.CurrentMode = mode;
            CoreStatusText.Text = result.CoreRunning
                ? LocalizationService.Instance.GetString("Master.Status.Running")
                : LocalizationService.Instance.GetString("Master.Status.NotRunning");
            SystemProxyStatusText.Text = result.SystemProxyEnabled
                ? LocalizationService.Instance.GetString("Master.Status.On")
                : LocalizationService.Instance.GetString("Master.Status.Off");
            TransparentProxyStatusText.Text = result.TransparentProxyEnabled
                ? LocalizationService.Instance.GetString("Master.Status.Running")
                : AppSettingsService.Instance.TransparentProxyEnabled
                    ? LocalizationService.Instance.GetString("Master.Status.Fallback")
                    : LocalizationService.Instance.GetString("Master.Status.Off");
            LogStorageService.Instance.AppendLog("Info", "MasterControl", result.Message, null);
            UpdateModeButtons();
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            _selectedMode = ClashSharpMode.Faulted;
            CoreStatusText.Text = LocalizationService.Instance.GetString("Master.Status.CoreStartFailed");
            LogStorageService.Instance.AppendLog("Error", "MasterControl", "Failed to apply selected Clash# mode.", exception.Message);
            UpdateModeButtons();
        }
    }

    /// <summary>Refreshes the checked state of all primary status buttons.</summary>
    private void UpdateModeButtons()
    {
        DisabledModeButton.IsChecked = _selectedMode == ClashSharpMode.Disabled;
        StandbyModeButton.IsChecked = _selectedMode == ClashSharpMode.Standby;
        RuleTakeoverModeButton.IsChecked = _selectedMode == ClashSharpMode.RuleTakeover;
        FullTakeoverModeButton.IsChecked = _selectedMode == ClashSharpMode.FullTakeover;
    }

    /// <summary>Refreshes visible proxy and transparent-proxy state from services and settings.</summary>
    private void RefreshProxyStatus()
    {
        try
        {
            WindowsProxyState proxyState = WindowsProxyService.Instance.GetCurrentState();
            SystemProxyStatusText.Text = proxyState.IsEnabled
                ? LocalizationService.Instance.GetString("Master.Status.On")
                : LocalizationService.Instance.GetString("Master.Status.Off");
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            SystemProxyStatusText.Text = LocalizationService.Instance.GetString("Master.Status.Unavailable");
        }

        TransparentProxyStatusText.Text = AppSettingsService.Instance.TransparentProxyEnabled
            ? LocalizationService.Instance.GetString("Master.Status.Standby")
            : LocalizationService.Instance.GetString("Master.Status.Off");
    }
}
