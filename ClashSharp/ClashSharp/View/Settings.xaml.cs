/*
 * Settings Page
 * Provides application settings for language, proxy behavior, and Windows integration
 *
 * @author: WaterRun
 * @file: View/Settings.xaml.cs
 * @date: 2026-06-17
 */

using System;
using System.Net.Http;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

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
            AppThemeService.Apply,
            ConnectionSamplingService.Instance.RestartFromSettings,
            isEnabled => _ = StartupLaunchService.Instance.SetEnabledAsync(isEnabled),
            LocalizationService.Instance.GetString,
            SettingsProxyInformationAdapter.CreateSnapshot,
            diagnosticsViewModel,
            new MihomoServiceControllerAdapter(MihomoServiceManager.Instance));
        InitializeComponent();
        DataContext = _viewModel;
        LoadSettings();
    }

    /// <summary>Loads persisted settings into visible controls.</summary>
    private void LoadSettings()
    {
        _viewModel.Load();
    }

    /// <summary>Persists the connection test URL when the input loses focus.</summary>
    /// <param name="sender">URL text box. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private void ConnectionTestUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.SetConnectionTestUrl(ConnectionTestUrlBox.Text))
        {
            ConnectionTestUrlBox.Text = _viewModel.ConnectionTestUrl;
        }
    }

    /// <summary>Runs a connection test against the configured test URL.</summary>
    private async void ConnectionTestButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionTestUrlBox_LostFocus(ConnectionTestUrlBox, e);
        ConnectionTestButton.IsEnabled = false;
        try
        {
            using HttpClient client = new()
            {
                Timeout = TimeSpan.FromSeconds(8),
            };
            using HttpResponseMessage response = await client.GetAsync(new Uri(_viewModel.ConnectionTestUrl));
            string message = string.Format(
                LocalizationService.Instance.GetString("Settings.ConnectionTest.Succeeded.Format"),
                (int)response.StatusCode);
            await ShowConnectionTestResultAsync(message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            string message = string.Format(
                LocalizationService.Instance.GetString("Settings.ConnectionTest.Failed.Format"),
                exception.Message);
            await ShowConnectionTestResultAsync(message);
        }
        finally
        {
            ConnectionTestButton.IsEnabled = true;
        }
    }

    /// <summary>Shows the connection test result.</summary>
    private async Task ShowConnectionTestResultAsync(string message)
    {
        ContentDialog dialog = new()
        {
            Title = _viewModel.ConnectionTestUrlTitleText,
            Content = message,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Close"),
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>Opens the Windows-native network repair dialog.</summary>
    /// <param name="sender">Clicked button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void OpenNetworkRepairButton_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog dialog = new()
        {
            Title = _viewModel.WindowsNativeTitleText,
            Content = BuildNetworkRepairPanel(),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Close"),
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>Builds the network repair dialog content.</summary>
    /// <returns>Dialog content panel.</returns>
    private ScrollViewer BuildNetworkRepairPanel()
    {
        StackPanel panel = new()
        {
            Spacing = 8,
            MinWidth = 360,
            MaxWidth = 640,
        };

        AddDiagnosticRow(panel, _viewModel.WslDiagnosticTitleText, nameof(SettingsViewModel.WslDiagnosticStatusText), "Wsl");
        AddDiagnosticRow(panel, _viewModel.TerminalDiagnosticTitleText, nameof(SettingsViewModel.TerminalDiagnosticStatusText), "Terminal");
        AddDiagnosticRow(panel, _viewModel.StoreDiagnosticTitleText, nameof(SettingsViewModel.StoreDiagnosticStatusText), "MicrosoftStore");

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(240, XamlRoot.Size.Height - 220),
            Padding = new Thickness(0, 0, 12, 0),
        };
    }

    /// <summary>Adds one diagnostic target row to the dialog panel.</summary>
    /// <param name="panel">Target panel. Must not be null.</param>
    /// <param name="title">Target title. Must not be null.</param>
    /// <param name="statusPropertyName">Bindable status property name. Must not be null.</param>
    /// <param name="targetTag">Diagnostic target tag. Must not be null.</param>
    private void AddDiagnosticRow(StackPanel panel, string title, string statusPropertyName, string targetTag)
    {
        Grid row = new()
        {
            Style = (Style)Application.Current.Resources["ClashCardGridStyle"],
            MinHeight = 68,
            RowSpacing = 8,
        };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel textPanel = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        });

        TextBlock statusText = new()
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        statusText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(statusPropertyName) });
        textPanel.Children.Add(statusText);
        row.Children.Add(textPanel);

        StackPanel buttonPanel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetRow(buttonPanel, 1);
        AddDiagnosticButton(buttonPanel, "\uE9D9", _viewModel.DiagnoseText, $"{targetTag}:Diagnose");
        AddDiagnosticButton(buttonPanel, "\uE73E", _viewModel.ApplyText, $"{targetTag}:Apply");
        AddDiagnosticButton(buttonPanel, "\uE72C", _viewModel.ResetText, $"{targetTag}:Reset");
        row.Children.Add(buttonPanel);

        panel.Children.Add(row);
    }

    /// <summary>Adds one command button to a diagnostic row.</summary>
    private void AddDiagnosticButton(StackPanel panel, string glyph, string text, string commandParameter)
    {
        Button button = new()
        {
            Command = _viewModel.WindowsDiagnosticCommand,
            CommandParameter = commandParameter,
            VerticalAlignment = VerticalAlignment.Center,
        };

        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });
        button.Content = content;

        panel.Children.Add(button);
    }

    /// <summary>Shows a one-step confirmation and restores all settings to defaults.</summary>
    private async void ResetAllSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Settings.ResetAllSettings.Title"),
            Content = LocalizationService.Instance.GetString("Settings.ResetAllSettings.Confirm"),
            PrimaryButtonText = _viewModel.ResetText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        AppDataMaintenanceService.ResetAllSettings();
        LocalizationService.Instance.CurrentLanguage = AppSettingsService.Instance.DisplayLanguage;
        _viewModel.SetDisplayLanguageIndex(ToLanguageIndex(AppSettingsService.Instance.DisplayLanguage));
        _viewModel.Load();
        ConnectionTestUrlBox.Text = _viewModel.ConnectionTestUrl;
    }

    /// <summary>Shows a two-step confirmation and clears all local application data.</summary>
    private async void ClearAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog firstDialog = new()
        {
            Title = LocalizationService.Instance.GetString("Settings.ClearAllData.Title"),
            Content = LocalizationService.Instance.GetString("Settings.ClearAllData.Confirm"),
            PrimaryButtonText = _viewModel.CleanupText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await firstDialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        ContentDialog secondDialog = new()
        {
            Title = LocalizationService.Instance.GetString("Settings.ClearAllData.SecondConfirm.Title"),
            Content = LocalizationService.Instance.GetString("Settings.ClearAllData.SecondConfirm"),
            PrimaryButtonText = _viewModel.CleanupText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await secondDialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        AppDataMaintenanceService.ClearAllData();
        LocalizationService.Instance.CurrentLanguage = AppSettingsService.Instance.DisplayLanguage;
        _viewModel.SetDisplayLanguageIndex(ToLanguageIndex(AppSettingsService.Instance.DisplayLanguage));
        _viewModel.Load();
        ConnectionTestUrlBox.Text = _viewModel.ConnectionTestUrl;
    }

    /// <summary>Maps language settings to the settings combo-box index.</summary>
    private static int ToLanguageIndex(AppLanguage language)
    {
        return language == AppLanguage.AutoDetect ? 0 : (int)language + 1;
    }

}
