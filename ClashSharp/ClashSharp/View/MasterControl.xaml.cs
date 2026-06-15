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
        PageTitleText.Text = LocalizationService.Instance.GetString("Nav.MasterControl");
        DescriptionText.Text = LocalizationService.Instance.GetString("Page.MasterControl.Description");
        UpdateModeButtons();
        Loaded += OnLoaded;
    }

    /// <summary>Handles page loading by probing the bundled mihomo core version for the status summary.</summary>
    /// <param name="sender">The loaded page instance. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ClashSharpMode configurationMode = _selectedMode == ClashSharpMode.Disabled ? ClashSharpMode.Standby : _selectedMode;
            CoreConfigurationState configurationState = CoreConfigurationService.Instance.EnsureConfiguration(configurationMode);
            CoreConfigurationText.Text = configurationState.Exists ? configurationState.ConfigPath : "配置不可用";

            string versionText = await MihomoCoreService.Instance.GetVersionTextAsync(CancellationToken.None);
            CoreStatusText.Text = $"就绪 · {versionText}";
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException)
        {
            CoreStatusText.Text = "核心不可用";
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
            CoreStatusText.Text = result.CoreRunning ? "运行中" : "未运行";
            SystemProxyStatusText.Text = result.SystemProxyEnabled ? "开启" : "关闭";
            TransparentProxyStatusText.Text = AppSettingsService.Instance.TransparentProxyEnabled ? "已在设置中启用" : "关闭";
            LogStorageService.Instance.AppendLog("Info", "MasterControl", result.Message, null);
            UpdateModeButtons();
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            _selectedMode = ClashSharpMode.Faulted;
            CoreStatusText.Text = "核心启动失败";
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
            SystemProxyStatusText.Text = proxyState.IsEnabled ? "开启" : "关闭";
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            SystemProxyStatusText.Text = "不可用";
        }

        TransparentProxyStatusText.Text = AppSettingsService.Instance.TransparentProxyEnabled ? "已在设置中启用" : "关闭";
    }
}
