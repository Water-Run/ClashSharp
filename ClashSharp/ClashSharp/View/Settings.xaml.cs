/*
 * Settings Page
 * Provides application settings for language, proxy behavior, and Windows integration
 *
 * @author: WaterRun
 * @file: View/Settings.xaml.cs
 * @date: 2026-06-17
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClashSharp.Model;
using ClashSharp.Components;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashSharp.View;

/// <summary>Page for application-wide settings such as language, Windows proxy behavior, and core configuration.</summary>
/// <remarks>
/// Invariants: Loaded controls mirror <see cref="AppSettingsService"/> values after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Persists settings when user-facing controls change.
/// </remarks>
public sealed partial class Settings : Page
{
    private sealed record DataPackageScopeOption(DataPackageExportScope Scope, string Title, string Description, string Glyph);

    /// <summary>Owns settings state transitions and persistence.</summary>
    private readonly SettingsViewModel _viewModel;

    /// <summary>True while initial settings are being bound to controls.</summary>
    private bool _isLoadingSettings = true;

    /// <summary>True while a restart-required switch is being restored after cancellation.</summary>
    private bool _isRevertingRestartSwitch;

    /// <summary>Initializes the settings page and applies localized text.</summary>
    public Settings()
    {
        SettingsDiagnosticsViewModel diagnosticsViewModel = new(
            new WindowsDiagnosticsClient(WindowsNetworkDiagnosticService.Instance),
            new DiagnosticsLog(LogStorageService.Instance),
            LocalizationService.Instance.GetString);
        _viewModel = new(
            new AppSettingsStore(AppSettingsService.Instance),
            language => LocalizationService.Instance.CurrentLanguage = language,
            AppThemeService.Apply,
            ConnectionSamplingService.Instance.RestartFromSettings,
            isEnabled => _ = StartupLaunchService.Instance.SetEnabledAsync(isEnabled),
            LocalizationService.Instance.GetString,
            SettingsProxyInformationAdapter.CreateSnapshot,
            diagnosticsViewModel,
            new MihomoServiceControllerAdapter(MihomoServiceManager.Instance),
            AppThemeService.ApplyAccentColor,
            resetAllSettings: AppDataMaintenanceService.ResetAllSettings,
            clearAllData: AppDataMaintenanceService.ClearAllData,
            checkStartupConflicts: StartupConflictDetectionService.Instance.CheckConflicts,
            isAccentColorRestartPending: AppThemeService.IsAccentColorRestartPending,
            notifyConnectionTestTimeout: NotificationService.Instance.NotifyConnectionTestTimeout,
            appendLog: LogStorageService.Instance.AppendLog);
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnUnloaded;
        LoadSettings();
        _isLoadingSettings = false;
        UpdateRestartRequiredState();
    }

    /// <summary>Loads persisted settings into visible controls.</summary>
    private void LoadSettings()
    {
        _viewModel.Load();
        UpdateRestartRequiredState();
    }

    /// <summary>Updates shell restart-required state when settings requiring restart change.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.HasRestartRequiredSettings))
        {
            UpdateRestartRequiredState();
        }
    }

    /// <summary>Publishes the current restart-required settings state to the shell.</summary>
    private void UpdateRestartRequiredState()
    {
        RestartRequiredStateService.Instance.SetRestartPending(_viewModel.HasRestartRequiredSettings);
    }

    /// <summary>Stops listening to view model notifications when the page leaves the visual tree.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Unloaded -= OnUnloaded;
    }

    /// <summary>Returns the window-level XAML root so dialogs center in the visible window.</summary>
    /// <returns>Window root when available; otherwise the page root.</returns>
    private XamlRoot GetDialogXamlRoot()
    {
        return App.MainWindow?.Content is FrameworkElement root && root.XamlRoot is not null
            ? root.XamlRoot
            : XamlRoot;
    }

    /// <summary>Runs a connection test against the configured test URL.</summary>
    private async void ConnectionTestButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionTestButton.IsEnabled = false;
        try
        {
            ConnectionTestReport report = await _viewModel.RunConnectionTestAsync(CancellationToken.None);
            await ShowConnectionTestResultAsync(report);
        }
        finally
        {
            ConnectionTestButton.IsEnabled = true;
        }
    }

    /// <summary>Shows the connection test result.</summary>
    private async Task ShowConnectionTestResultAsync(ConnectionTestReport report)
    {
        await CenteredDialogOverlay.ShowAsync(
            GetDialogXamlRoot(),
            _viewModel.ConnectionTestUrlTitleText,
            BuildConnectionTestResultPanel(report),
            LocalizationService.Instance.GetString("Command.Close"),
            720);
    }

    private StackPanel BuildConnectionTestResultPanel(ConnectionTestReport report)
    {
        StackPanel panel = new()
        {
            Spacing = 12,
            MinWidth = 520,
            MaxWidth = 680,
        };

        Grid table = new()
        {
            RowSpacing = 8,
            ColumnSpacing = 12,
        };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddConnectionTestHeader(table);

        foreach (ConnectionTestTargetResult result in report.Results)
        {
            AddConnectionTestResultRow(table, result);
        }

        panel.Children.Add(table);
        panel.Children.Add(new TextBlock
        {
            Text = report.SummaryText,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Foreground = GetConnectionTestSummaryBrush(report.SummaryState),
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        return panel;
    }

    private void AddConnectionTestHeader(Grid table)
    {
        int rowIndex = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddConnectionTestText(table, rowIndex, 0, "URL", "CaptionTextBlockStyle", "TextFillColorSecondaryBrush");
        AddConnectionTestText(table, rowIndex, 1, _viewModel.ConnectionTestStatusColumnText, "CaptionTextBlockStyle", "TextFillColorSecondaryBrush");
        AddConnectionTestText(table, rowIndex, 2, _viewModel.ConnectionTestLatencyColumnText, "CaptionTextBlockStyle", "TextFillColorSecondaryBrush");
    }

    private static void AddConnectionTestResultRow(Grid table, ConnectionTestTargetResult result)
    {
        int rowIndex = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddConnectionTestText(table, rowIndex, 0, result.Url, "BodyTextBlockStyle", "TextFillColorPrimaryBrush");

        StackPanel statusPanel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusPanel.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(result.Succeeded ? Windows.UI.Color.FromArgb(255, 16, 124, 16) : Windows.UI.Color.FromArgb(255, 196, 43, 28)),
        });
        statusPanel.Children.Add(new TextBlock
        {
            Text = result.StatusText,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });
        Grid.SetRow(statusPanel, rowIndex);
        Grid.SetColumn(statusPanel, 1);
        table.Children.Add(statusPanel);

        AddConnectionTestText(table, rowIndex, 2, result.LatencyText, "BodyTextBlockStyle", "TextFillColorPrimaryBrush");
    }

    private static Brush GetConnectionTestSummaryBrush(ConnectionTestSummaryState summaryState)
    {
        return summaryState switch
        {
            ConnectionTestSummaryState.AllPassed => ResourceBrush(
                "SystemFillColorSuccessBrush",
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16))),
            ConnectionTestSummaryState.PartialFailed => ResourceBrush(
                "SystemFillColorCautionBrush",
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 157, 93, 0))),
            ConnectionTestSummaryState.AllFailed => ResourceBrush(
                "SystemFillColorCriticalBrush",
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28))),
            _ => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
    }

    private static void AddConnectionTestText(Grid table, int rowIndex, int columnIndex, string text, string styleKey, string brushKey)
    {
        TextBlock textBlock = new()
        {
            Text = text,
            Style = (Style)Application.Current.Resources[styleKey],
            Foreground = (Brush)Application.Current.Resources[brushKey],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(textBlock, rowIndex);
        Grid.SetColumn(textBlock, columnIndex);
        table.Children.Add(textBlock);
    }

    private static Brush ResourceBrush(string key, Brush fallback)
    {
        return Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
            ? brush
            : fallback;
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
            XamlRoot = GetDialogXamlRoot(),
        };

        await dialog.ShowAsync();
    }

    /// <summary>Shows restart guidance when accent color mode changes after initial binding.</summary>
    /// <param name="sender">Accent color mode combo box. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AppAccentColorModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || !_viewModel.IsAppAccentColorRestartPending)
        {
            return;
        }

        await ShowRestartRequiredDialogAsync();
    }

    /// <summary>Opens the application accent color picker and persists the selected color.</summary>
    /// <param name="sender">Clicked color swatch button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AppAccentColorSwatchButton_Click(object sender, RoutedEventArgs e)
    {
        ColorPicker picker = new()
        {
            Color = AppThemeService.ParseAccentColorOrDefault(_viewModel.AppAccentColorValue),
            IsAlphaEnabled = false,
            Width = 320,
            MaxWidth = 320,
        };

        Grid pickerPanel = new()
        {
            Width = 340,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pickerPanel.Children.Add(picker);

        ContentDialog dialog = new()
        {
            Title = _viewModel.AppAccentColorTitleText,
            Content = pickerPanel,
            MaxWidth = 420,
            PrimaryButtonText = _viewModel.AppAccentColorPickText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = GetDialogXamlRoot(),
        };

        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return;
        }

        _viewModel.SetAppAccentColorModeIndex((int)AppAccentColorMode.Custom);
        _viewModel.SetAppAccentColorValue(AppThemeService.FormatAccentColor(picker.Color));
        if (_viewModel.IsAppAccentColorRestartPending)
        {
            await ShowRestartRequiredDialogAsync();
        }
    }

    /// <summary>Opens the connection-test URL editor dialog.</summary>
    private async void EditConnectionTestUrlsButton_Click(object sender, RoutedEventArgs e)
    {
        TextBox proxyUrl1Box = new() { Text = _viewModel.ConnectionTestProxyUrl1, Width = 360 };
        TextBox proxyUrl2Box = new() { Text = _viewModel.ConnectionTestProxyUrl2, Width = 360 };
        TextBox directUrlBox = new() { Text = _viewModel.ConnectionTestDirectUrl, Width = 360 };
        StackPanel panel = BuildConnectionTestUrlsPanel(proxyUrl1Box, proxyUrl2Box, directUrlBox);

        ContentDialog dialog = new()
        {
            Title = _viewModel.ConnectionTestUrlTitleText,
            Content = panel,
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Save"),
            SecondaryButtonText = _viewModel.ResetText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = GetDialogXamlRoot(),
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Secondary)
        {
            _viewModel.ResetConnectionTestUrlsToDefaults();
            return;
        }

        if (result is ContentDialogResult.Primary)
        {
            _viewModel.SetConnectionTestUrls(proxyUrl1Box.Text, proxyUrl2Box.Text, directUrlBox.Text);
        }
    }

    /// <summary>Builds the connection-test URL editor content.</summary>
    private StackPanel BuildConnectionTestUrlsPanel(TextBox proxyUrl1Box, TextBox proxyUrl2Box, TextBox directUrlBox)
    {
        StackPanel panel = new()
        {
            Spacing = 10,
            MinWidth = 420,
        };
        AddConnectionTestUrlEditorRow(panel, _viewModel.ConnectionTestProxyUrl1TitleText, proxyUrl1Box);
        AddConnectionTestUrlEditorRow(panel, _viewModel.ConnectionTestProxyUrl2TitleText, proxyUrl2Box);
        AddConnectionTestUrlEditorRow(panel, _viewModel.ConnectionTestDirectUrlTitleText, directUrlBox);

        Button restoreButton = new()
        {
            Name = "RestoreConnectionTestUrlsButton",
            Content = _viewModel.ResetText,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        restoreButton.Click += (_, _) =>
        {
            proxyUrl1Box.Text = "https://www.google.com";
            proxyUrl2Box.Text = "https://github.com";
            directUrlBox.Text = "https://www.baidu.com";
        };
        panel.Children.Add(restoreButton);
        return panel;
    }

    private static void AddConnectionTestUrlEditorRow(StackPanel panel, string label, TextBox textBox)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        panel.Children.Add(textBox);
    }

    /// <summary>Shows a short prompt explaining that the edited setting applies after restart.</summary>
    private async Task ShowRestartRequiredDialogAsync()
    {
        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Settings.RestartRequired.Title"),
            Content = LocalizationService.Instance.GetString("Settings.RestartRequired.Message"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Close"),
            XamlRoot = GetDialogXamlRoot(),
        };

        await dialog.ShowAsync();
    }

    /// <summary>Confirms a setting change that is persisted now but only applied after restart.</summary>
    private async Task<bool> ConfirmRestartRequiredSettingChangeAsync()
    {
        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Settings.RestartSettingConfirm.Title"),
            Content = LocalizationService.Instance.GetString("Settings.RestartSettingConfirm.Message"),
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Apply"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        return await dialog.ShowAsync() is ContentDialogResult.Primary;
    }

    private async void TriggersEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || _isRevertingRestartSwitch || sender is not ToggleSwitch toggle || toggle.IsOn == _viewModel.TriggersEnabled)
        {
            return;
        }

        bool previousValue = _viewModel.TriggersEnabled;
        if (!await ConfirmRestartRequiredSettingChangeAsync())
        {
            RestoreRestartSwitch(toggle, previousValue);
            return;
        }

        _viewModel.SetTriggersEnabled(toggle.IsOn);
        UpdateRestartRequiredState();
    }

    private async void TrayUseMonochromeInactiveIconToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || _isRevertingRestartSwitch || sender is not ToggleSwitch toggle || toggle.IsOn == _viewModel.TrayUseMonochromeInactiveIcon)
        {
            return;
        }

        bool previousValue = _viewModel.TrayUseMonochromeInactiveIcon;
        if (!await ConfirmRestartRequiredSettingChangeAsync())
        {
            RestoreRestartSwitch(toggle, previousValue);
            return;
        }

        _viewModel.SetTrayUseMonochromeInactiveIcon(toggle.IsOn);
        UpdateRestartRequiredState();
    }

    private void RestoreRestartSwitch(ToggleSwitch toggle, bool value)
    {
        _isRevertingRestartSwitch = true;
        try
        {
            toggle.IsOn = value;
        }
        finally
        {
            _isRevertingRestartSwitch = false;
        }
    }

    private async void ResetBasicSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetBasicSettingsToDefaults);
    }

    private async void ResetStartupSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetStartupSettingsToDefaults);
    }

    private async void ResetNotificationSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetNotificationSettingsToDefaults);
    }

    private async void ResetTriggerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetTriggerSettingsToDefaults);
    }

    private async void ResetTraySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetTraySettingsToDefaults);
    }

    private async void ResetTransparentProxySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetTransparentProxySettingsToDefaults, includeServiceDeploymentNote: true);
    }

    private async void ResetProxySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetProxySettingsToDefaults, includeServiceDeploymentNote: true);
    }

    private async void ResetWindowsNativeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetWindowsNativeSettingsToDefaults, includeServiceDeploymentNote: true);
    }

    private async void ResetMainlandChinaSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetMainlandChinaSettingsToDefaults);
    }

    /// <summary>Confirms and applies a settings-group default reset.</summary>
    /// <param name="resetAction">Group reset action. Must not be null.</param>
    private async Task ResetSettingsGroupAsync(Action resetAction, bool includeServiceDeploymentNote = false)
    {
        ArgumentNullException.ThrowIfNull(resetAction);
        string message = _viewModel.ResetGroupConfirmMessageText;
        if (includeServiceDeploymentNote)
        {
            message = $"{message}{Environment.NewLine}{Environment.NewLine}{_viewModel.ResetGroupServiceDeploymentNoteText}";
        }

        ContentDialog dialog = new()
        {
            Title = _viewModel.ResetGroupConfirmTitleText,
            Content = message,
            PrimaryButtonText = _viewModel.ResetGroupToDefaultsText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            resetAction();
        }
    }

    /// <summary>Opens the searchable tray feature selector.</summary>
    private async void EditTrayVisibleFeaturesButton_Click(object sender, RoutedEventArgs e)
    {
        HashSet<string> selectedIds = new(
            _viewModel.TrayVisibleFeatureIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        SearchableOptionList optionList = new()
        {
            SearchPlaceholder = _viewModel.TrayVisibleFeatureSearchPlaceholderText,
            AllowMultiple = true,
            MaxListHeight = 360,
        };
        optionList.SetOptions(SettingsViewModel.TrayFeatureDefinitions.Select(feature => new SearchableOptionItem(
            feature.Id,
            LocalizationService.Instance.GetString(feature.TitleKey),
            _viewModel.TraySectionTitleText,
            LocalizationService.Instance.GetString(feature.DescriptionKey),
            feature.Glyph,
            feature.Id,
            selectedIds.Contains(feature.Id))));

        ContentDialog dialog = new()
        {
            Title = _viewModel.TrayVisibleFeaturesTitleText,
            Content = optionList,
            PrimaryButtonText = LocalizationService.Instance.GetString("Command.Save"),
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = GetDialogXamlRoot(),
        };

        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            _viewModel.SetTrayVisibleFeatureIds(optionList.SelectedOptions.Select(static option => option.Id));
        }
    }

    /// <summary>Runs startup conflict detection immediately and shows the shared result dialog.</summary>
    /// <param name="sender">Clicked button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void CheckStartupConflictsButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StartupConflictIssue> issues = _viewModel.CheckStartupConflicts();
        await StartupConflictDialogPresenter.ShowAsync(GetDialogXamlRoot(), issues);
    }

    /// <summary>Shows the startup prompt immediately.</summary>
    private async void ShowStartupPromptButton_Click(object sender, RoutedEventArgs e)
    {
        XamlRoot xamlRoot = GetDialogXamlRoot();
        StartupGuideDialog dialog = new();
        await dialog.ShowCenteredAsync(xamlRoot);
    }

    /// <summary>Registers the startup restore fallback helper.</summary>
    private void RegisterStartupRestoreFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RegisterStartupRestoreFallback();
    }

    /// <summary>Refreshes startup restore fallback registration status.</summary>
    private void DetectStartupRestoreFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshStartupRestoreFallbackStatus();
    }

    /// <summary>Uninstalls the startup restore fallback helper.</summary>
    private void UninstallStartupRestoreFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.UninstallStartupRestoreFallback();
    }

    /// <summary>Exports a Clash# XML data package with the selected scope.</summary>
    private async void ExportDataPackageButton_Click(object sender, RoutedEventArgs e)
    {
        DataPackageExportScope? scope = await SelectDataPackageExportScopeAsync();
        if (scope is DataPackageExportScope selectedScope)
        {
            await PickAndExportDataPackageAsync(selectedScope);
        }
    }

    /// <summary>Imports a Clash# XML data package after two confirmations.</summary>
    private async void ImportDataPackageButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        InitializePickerWithWindow(picker);
        picker.FileTypeFilter.Add(".xml");

        StorageFile? file = await picker.PickSingleFileAsync();
        ClashDataPackageScope? scope = ReadPackageScope(file?.Path ?? string.Empty);
        if (file is null || !IsImportableDataPackageScope(scope) || !await ConfirmDataImportAsync(scope))
        {
            return;
        }

        await ClashDataPackageService.Instance.ImportAsync(file.Path, CancellationToken.None);
        ApplyImportedSettings();
    }

    /// <summary>Prompts for the package export scope immediately before saving.</summary>
    private async Task<DataPackageExportScope?> SelectDataPackageExportScopeAsync()
    {
        StackPanel optionPanel = new()
        {
            Spacing = 8,
        };
        List<DialogOptionRow> rows = [];

        foreach (DataPackageScopeOption option in BuildDataPackageScopeOptions())
        {
            DialogOptionRow row = new()
            {
                Title = option.Title,
                Metadata = _viewModel.DataExportTitleText,
                Description = option.Description,
                Glyph = option.Glyph,
                IsChecked = option.Scope == DataPackageExportScope.Settings,
                Tag = option.Scope,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            row.SelectionInvoked += (_, _) => SelectDataPackageScopeRow(row, rows);
            rows.Add(row);
            optionPanel.Children.Add(row);
        }

        StackPanel panel = new()
        {
            Spacing = 12,
            MinWidth = 420,
            MaxWidth = 620,
        };
        panel.Children.Add(new TextBlock
        {
            Text = _viewModel.DataExportDescriptionText,
            TextWrapping = TextWrapping.WrapWholeWords,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        });
        panel.Children.Add(optionPanel);

        ContentDialog dialog = new()
        {
            Title = LocalizationService.Instance.GetString("Settings.DataExport.Title"),
            Content = panel,
            PrimaryButtonText = _viewModel.ExportText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = GetDialogXamlRoot(),
        };

        if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return null;
        }

        foreach (DialogOptionRow row in rows)
        {
            if (row.IsChecked && row.Tag is DataPackageExportScope scope)
            {
                return scope;
            }
        }

        return DataPackageExportScope.Settings;
    }

    private IReadOnlyList<DataPackageScopeOption> BuildDataPackageScopeOptions()
    {
        return
        [
            new(
                DataPackageExportScope.Settings,
                _viewModel.DataPackageScopeSettingsText,
                LocalizationService.Instance.GetString("Settings.DataPackage.Scope.Settings.Description"),
                "\uE713"),
            new(
                DataPackageExportScope.SettingsAndProxyConfiguration,
                _viewModel.DataPackageScopeSettingsAndProxyConfigurationText,
                LocalizationService.Instance.GetString("Settings.DataPackage.Scope.SettingsAndProxyConfiguration.Description"),
                "\uE968"),
            new(
                DataPackageExportScope.SystemLogSqlite,
                LocalizationService.Instance.GetString("Settings.DataPackage.Scope.SystemLogSqlite"),
                LocalizationService.Instance.GetString("Settings.DataPackage.Scope.SystemLogSqlite.Description"),
                "\uE777"),
        ];
    }

    private static void SelectDataPackageScopeRow(DialogOptionRow selectedRow, IReadOnlyList<DialogOptionRow> rows)
    {
        foreach (DialogOptionRow row in rows)
        {
            row.IsChecked = ReferenceEquals(row, selectedRow);
        }
    }

    /// <summary>Shows a save picker and exports the selected data package scope.</summary>
    private async Task PickAndExportDataPackageAsync(DataPackageExportScope scope)
    {
        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"ClashSharp-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        InitializePickerWithWindow(picker);
        if (scope == DataPackageExportScope.SystemLogSqlite)
        {
            picker.FileTypeChoices.Add("SQLite", [".sqlite3"]);
        }
        else
        {
            picker.FileTypeChoices.Add("Clash# XML", [".xml"]);
        }

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        switch (scope)
        {
            case DataPackageExportScope.Settings:
                await ClashDataPackageService.Instance.ExportAsync(file.Path, ClashDataPackageScope.Settings, CancellationToken.None);
                break;
            case DataPackageExportScope.SettingsAndProxyConfiguration:
                await ClashDataPackageService.Instance.ExportAsync(file.Path, ClashDataPackageScope.SettingsAndProxyConfiguration, CancellationToken.None);
                break;
            case DataPackageExportScope.SystemLogSqlite:
                await ExportLogSqliteAsync(file.Path, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported export scope.");
        }
    }

    private static async Task ExportLogSqliteAsync(string path, CancellationToken cancellationToken)
    {
        LogStorageService.Instance.GetStorageSummary();
        await using FileStream source = File.OpenRead(LogStorageService.Instance.DatabasePath);
        await using FileStream target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static bool IsImportableDataPackageScope(ClashDataPackageScope? scope)
    {
        return scope is ClashDataPackageScope.Settings or ClashDataPackageScope.SettingsAndProxyConfiguration;
    }

    /// <summary>Confirms import overwrite behavior in two steps.</summary>
    private async Task<bool> ConfirmDataImportAsync(ClashDataPackageScope? scope)
    {
        ContentDialog firstDialog = new()
        {
            Title = LocalizationService.Instance.GetString("Settings.DataImport.Warning.Title"),
            Content = FormatDataImportWarning(scope),
            PrimaryButtonText = _viewModel.ImportText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        if (await firstDialog.ShowAsync() is not ContentDialogResult.Primary)
        {
            return false;
        }

        ContentDialog secondDialog = new()
        {
            Title = LocalizationService.Instance.GetString("Settings.DataImport.SecondConfirm.Title"),
            Content = LocalizationService.Instance.GetString("Settings.DataImport.SecondConfirm.Message"),
            PrimaryButtonText = _viewModel.ImportText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        return await secondDialog.ShowAsync() is ContentDialogResult.Primary;
    }

    private static ClashDataPackageScope? ReadPackageScope(string packagePath)
    {
        try
        {
            string? scopeText = XDocument.Load(packagePath).Root?.Attribute("Scope")?.Value;
            return Enum.TryParse(scopeText, out ClashDataPackageScope scope)
                ? scope
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string FormatDataImportWarning(ClashDataPackageScope? scope)
    {
        string message = LocalizationService.Instance.GetString("Settings.DataImport.Warning.Message");
        if (scope is null)
        {
            return message;
        }

        return $"{message}{Environment.NewLine}{string.Format(LocalizationService.Instance.GetString("Settings.DataImport.Warning.Scope.Format"), GetDataPackageScopeText(scope.Value))}";
    }

    private string GetDataPackageScopeText(ClashDataPackageScope scope)
    {
        return scope switch
        {
            ClashDataPackageScope.Settings => _viewModel.DataPackageScopeSettingsText,
            ClashDataPackageScope.SettingsAndProxyConfiguration => _viewModel.DataPackageScopeSettingsAndProxyConfigurationText,
            _ => scope.ToString(),
        };
    }

    /// <summary>Re-applies settings that affect running application services after package import.</summary>
    private void ApplyImportedSettings()
    {
        AppSettingsService settings = AppSettingsService.Instance;
        LocalizationService.Instance.CurrentLanguage = settings.DisplayLanguage;
        AppThemeService.Apply(settings.AppThemeMode);
        AppThemeService.ApplyAccentColor(settings.AppAccentColorMode, settings.AppAccentColorValue);
        _ = StartupLaunchService.Instance.SetEnabledAsync(settings.LaunchAtStartupEnabled);
        ConnectionSamplingService.Instance.RestartFromSettings();
        _viewModel.Load();
    }

    /// <summary>Associates a WinUI picker with the application window so it can be shown from unpackaged desktop context.</summary>
    private static void InitializePickerWithWindow(object picker)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        nint windowHandle = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, windowHandle);
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

    /// <summary>Shows a two-step confirmation and restores all settings to defaults.</summary>
    private async void ResetAllSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                LocalizationService.Instance.GetString("Settings.ResetAllSettings.Title"),
                LocalizationService.Instance.GetString("Settings.ResetAllSettings.Confirm"),
                _viewModel.ResetText)
            || !await ConfirmAsync(
                LocalizationService.Instance.GetString("Settings.ResetAllSettings.SecondConfirm.Title"),
                LocalizationService.Instance.GetString("Settings.ResetAllSettings.SecondConfirm"),
                _viewModel.ResetText))
        {
            return;
        }

        _viewModel.ResetAllSettings();
    }

    /// <summary>Shows a three-step confirmation and clears all local application data.</summary>
    private async void ClearAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                LocalizationService.Instance.GetString("Settings.ClearAllData.Title"),
                LocalizationService.Instance.GetString("Settings.ClearAllData.Confirm"),
                _viewModel.CleanupText)
            || !await ConfirmAsync(
                LocalizationService.Instance.GetString("Settings.ClearAllData.SecondConfirm.Title"),
                LocalizationService.Instance.GetString("Settings.ClearAllData.SecondConfirm"),
                _viewModel.CleanupText)
            || !await ConfirmAsync(
                LocalizationService.Instance.GetString("Settings.ClearAllData.FinalConfirm.Title"),
                LocalizationService.Instance.GetString("Settings.ClearAllData.FinalConfirm"),
                _viewModel.CleanupText))
        {
            return;
        }

        _viewModel.ClearAllData();
    }

    /// <summary>Shows a destructive-action confirmation dialog.</summary>
    private async Task<bool> ConfirmAsync(string title, string content, string primaryButtonText)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = LocalizationService.Instance.GetString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        return await dialog.ShowAsync() is ContentDialogResult.Primary;
    }

}
