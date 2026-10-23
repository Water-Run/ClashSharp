using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.Settings;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ClashSharp.View;

/// <summary>Page for application-wide settings such as language, Windows proxy behavior, and core configuration.</summary>
/// <remarks>
/// Invariants: Loaded controls mirror the injected settings view model after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Persists settings when user-facing controls change.
/// </remarks>
public sealed partial class Settings : Page, ILocalizedPresentation
{
    /// <summary>Owns settings state transitions and persistence.</summary>
    private readonly SettingsViewModel _viewModel;
    private readonly SettingsRecoveryViewModel _recovery;
    private readonly McpSettingsViewModel _mcp;
    private readonly Func<string, string> _getString;
    private readonly Action<bool> _setRestartPending;
    private readonly Func<string, Windows.UI.Color> _parseAccentColor;
    private readonly Func<Windows.UI.Color, string> _formatAccentColor;
    private readonly IApplicationErrorSink _errorSink;
    private readonly IStartupGuidePresenter _startupGuide;
    private readonly DataPackageDialogPresenter _dataPackages;
    private readonly PageOperationSession _pageOperations;
    private readonly Func<Action, IDisposable> _subscribeToRuntimeSettingsChanges;
    private IDisposable? _runtimeSettingsSubscription;

    /// <summary>True while initial settings are being bound to controls.</summary>
    private bool _isLoadingSettings = true;

    private bool _isLoaded;
    private int _visit;
    private bool _isViewModelSubscribed;

    internal Settings(SettingsPageDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel
            ?? throw new ArgumentException("A settings view model is required.", nameof(dependencies));
        _recovery = dependencies.Recovery ?? throw new ArgumentException("Settings recovery state is required.", nameof(dependencies));
        _mcp = dependencies.Mcp ?? throw new ArgumentException("MCP management state is required.", nameof(dependencies));
        _getString = dependencies.GetString
            ?? throw new ArgumentException("A localization function is required.", nameof(dependencies));
        _setRestartPending = dependencies.SetRestartPending
            ?? throw new ArgumentException("A restart-state publisher is required.", nameof(dependencies));
        _parseAccentColor = dependencies.ParseAccentColor
            ?? throw new ArgumentException("An accent-color parser is required.", nameof(dependencies));
        _formatAccentColor = dependencies.FormatAccentColor
            ?? throw new ArgumentException("An accent-color formatter is required.", nameof(dependencies));
        _errorSink = dependencies.ErrorSink
            ?? throw new ArgumentException("An application error sink is required.", nameof(dependencies));
        _startupGuide = dependencies.StartupGuide
            ?? throw new ArgumentException("A startup-guide presenter is required.", nameof(dependencies));
        _dataPackages = dependencies.DataPackages
            ?? throw new ArgumentException("A data-package presenter is required.", nameof(dependencies));
        _pageOperations = new PageOperationSession(_errorSink, "settings-page-operation");
        _subscribeToRuntimeSettingsChanges = dependencies.SubscribeToRuntimeSettingsChanges
            ?? throw new ArgumentException("A runtime settings subscription is required.", nameof(dependencies));
        InitializeComponent();
        ApplicationRecoveryPanel.DataContext = _recovery;
        McpPanel.DataContext = _mcp;
        McpFeedbackHost.DataContext = _mcp;
        _recovery.Refresh();
        // Bind persisted choices on the first frame, before the asynchronous page queue runs.
        _viewModel.Load();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Loads persisted settings into visible controls.</summary>
    private void LoadSettings()
    {
        _viewModel.Load();
        _recovery.Refresh();
        RefreshPreferenceControls();
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
        _setRestartPending(_viewModel.HasRestartRequiredSettings);
    }

    /// <summary>Restores page-scoped subscriptions and cancellation after navigation back to this instance.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        int visit = ++_visit;
        await _pageOperations.DrainAsync();
        if (!_isLoaded || visit != _visit)
        {
            return;
        }

        await RunPageOperationAsync(async token =>
        {
            SubscribeToViewModel();
            var dispatcher = DispatcherQueue;
            _runtimeSettingsSubscription = _subscribeToRuntimeSettingsChanges(
                () => dispatcher.TryEnqueue(() => RefreshRuntimeSettings(visit)));
            CheckStartupConflictsButton.IsEnabled = true;
            _isLoadingSettings = true;
            try
            {
                LoadSettings();
            }
            finally
            {
                _isLoadingSettings = false;
            }

            await _viewModel.RefreshMihomoServiceStatusCommand.ExecuteObservedAsync(null, token);
            await _viewModel.RefreshStartupRestoreFallbackStatusAsync(token);
            await _mcp.RefreshAsync(token);
        });
    }

    /// <summary>Stops page work and view-model notifications while the page is outside the visual tree.</summary>
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _runtimeSettingsSubscription?.Dispose();
        _runtimeSettingsSubscription = null;
        if (_isViewModelSubscribed)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ApplyLaunchAtStartupCommand.PropertyChanged -= OnRuntimeCommandPropertyChanged;
            _viewModel.ApplyNetworkSettingsCommand.PropertyChanged -= OnRuntimeCommandPropertyChanged;
            _viewModel.RestartConnectionSamplingCommand.PropertyChanged -= OnRuntimeCommandPropertyChanged;
            _isViewModelSubscribed = false;
        }

        _isLoaded = false;
        _visit++;
        _pageOperations.Cancel();
        await _pageOperations.DrainAsync();
    }

    /// <summary>Refreshes visible text immediately under the input guard, including behind an open modal editor.</summary>
    public void RefreshLocalization() => RefreshRuntimeSettings(_visit, projectCommitted: false);

    private async void RefreshRuntimeSettings(int visit, bool projectCommitted = true)
    {
        if (!_isLoaded || visit != _visit)
        {
            return;
        }

        try { RefreshRuntimePresentation(projectCommitted); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            await RunPageOperationAsync(token => _errorSink.ReportAsync(
                new ApplicationError("settings-runtime-presentation", failure), token));
            return;
        }

        await RunPageOperationAsync(async token =>
        {
            _recovery.Refresh();
            await _mcp.RefreshObservedAsync(token);
        });
    }

    private void RefreshRuntimePresentation(bool projectCommitted)
    {
        bool wasLoadingSettings = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            // Projection preserves in-flight choices. Text and selector binding notifications must
            // not wait for a modal operation, or be submitted as new user edits.
            // A language notification can occur inside the appearance transaction. Only the
            // separately dispatched committed-state notification reads authoritative choices.
            if (projectCommitted) { _viewModel.RefreshCommittedRuntimeSettings(); }
            _viewModel.RefreshLocalization();
            RefreshPreferenceControls();
            _recovery.RefreshLocalization();
            _mcp.RefreshLocalization();
        }
        finally { _isLoadingSettings = wasLoadingSettings; }
    }

    private void SubscribeToViewModel()
    {
        if (_isViewModelSubscribed)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ApplyLaunchAtStartupCommand.PropertyChanged += OnRuntimeCommandPropertyChanged;
        _viewModel.ApplyNetworkSettingsCommand.PropertyChanged += OnRuntimeCommandPropertyChanged;
        _viewModel.RestartConnectionSamplingCommand.PropertyChanged += OnRuntimeCommandPropertyChanged;
        _isViewModelSubscribed = true;
    }

    private void OnRuntimeCommandPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AsyncRelayCommand.IsRunning)
            && sender is AsyncRelayCommand { IsRunning: false })
        {
            // A background commit may have arrived while this page's request was still running.
            RefreshRuntimeSettings(_visit);
        }
    }

    private async void PreferenceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || !_isLoaded || !_viewModel.IsPreferenceInputEnabled || sender is not ToggleSwitch toggle) { return; }
        (SettingKey Key, bool Current) setting = toggle.Name switch
        {
            "StartupConflictCheckToggle" => (SettingsRegistry.Keys.StartupConflictCheckEnabled, _viewModel.StartupConflictCheckEnabled),
            "ShowStartupGuideToggle" => (SettingsRegistry.Keys.ShowStartupGuideOnStartup, _viewModel.ShowStartupGuideOnStartup),
            "CheckStaleProxyToggle" => (SettingsRegistry.Keys.CheckStaleProxyOnStartup, _viewModel.CheckStaleProxyOnStartup),
            "RestoreProxyOnExitToggle" => (SettingsRegistry.Keys.RestoreProxyOnExit, _viewModel.RestoreProxyOnExit),
            "NotificationEnabledToggle" => (SettingsRegistry.Keys.NotificationEnabled, _viewModel.NotificationEnabled),
            "TrayUseMonochromeInactiveIconToggle" => (SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon, _viewModel.TrayUseMonochromeInactiveIcon),
            "MainlandChinaUrlBlockingToggle" => (SettingsRegistry.Keys.MainlandChinaUrlBlockingEnabled, _viewModel.MainlandChinaUrlBlockingEnabled),
            "TriggersEnabledToggle" => (SettingsRegistry.Keys.TriggersEnabled, _viewModel.TriggersEnabled),
            "TriggerNotificationsEnabledToggle" => (SettingsRegistry.Keys.TriggerNotificationsEnabled, _viewModel.TriggerNotificationsEnabled),
            _ => throw new InvalidOperationException("The preference toggle is not mapped."),
        };
        bool requested = toggle.IsOn;
        if (requested == setting.Current) { return; }
        await ApplyPagePreferenceAsync(token => _viewModel.ApplyPreferenceAsync(setting.Key, requested, token));
    }

    private async void PreferenceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || !_isLoaded || !_viewModel.IsPreferenceInputEnabled || sender is not ComboBox combo) { return; }
        int index = combo.SelectedIndex;
        Func<CancellationToken, Task>? apply = combo.Name switch
        {
            "LanguageBox" when index != _viewModel.DisplayLanguageIndex && (index == 0 || index > 0 && Enum.IsDefined((AppLanguage)(index - 1))) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.DisplayLanguage, index == 0 ? AppLanguage.AutoDetect : (AppLanguage)(index - 1), token),
            "AppThemeModeBox" when index != _viewModel.AppThemeModeIndex && Enum.IsDefined((AppThemeMode)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.AppThemeMode, (AppThemeMode)index, token),
            "DataUnitStyleBox" when index != _viewModel.DataUnitStyleIndex && Enum.IsDefined((DataUnitStyle)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.DataUnitStyle, (DataUnitStyle)index, token),
            "RateUnitBox" when index != _viewModel.RateUnitIndex && Enum.IsDefined((RateUnit)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.RateUnit, (RateUnit)index, token),
            "ClockFormatBox" when index != _viewModel.ClockFormatIndex && Enum.IsDefined((ClockFormat)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.ClockFormat, (ClockFormat)index, token),
            "AppAccentColorModeBox" when index != _viewModel.AppAccentColorModeIndex && Enum.IsDefined((AppAccentColorMode)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.AppAccentColorMode, (AppAccentColorMode)index, token),
            "CloseBehaviorModeBox" when index != _viewModel.CloseBehaviorModeIndex && Enum.IsDefined((CloseBehaviorMode)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.CloseBehaviorMode, (CloseBehaviorMode)index, token),
            "StartupBehaviorModeBox" when index != _viewModel.StartupBehaviorModeIndex && Enum.IsDefined((StartupBehaviorMode)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.StartupBehaviorMode, (StartupBehaviorMode)index, token),
            "NotificationLevelBox" when index != _viewModel.NotificationLevelIndex && Enum.IsDefined((NotificationLevel)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.NotificationLevel, (NotificationLevel)index, token),
            "MainlandChinaFeatureModeBox" when index != _viewModel.MainlandChinaFeatureModeIndex && Enum.IsDefined((MainlandChinaFeatureMode)index) =>
                token => _viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.MainlandChinaFeatureMode, (MainlandChinaFeatureMode)index, token),
            _ => null,
        };
        if (apply is not null)
        {
            await ApplyPagePreferenceAsync(apply);
        }
    }

    private Task ApplyPagePreferenceAsync(Func<CancellationToken, Task> apply)
    {
        int visit = _visit;
        return RunPageOperationAsync(async token =>
        {
            try { await apply(token); }
            finally
            {
                if (_isLoaded && visit == _visit) { RefreshPreferenceControls(); }
            }
        });
    }

    private void RefreshPreferenceControls()
    {
        // Rebind only these choices. Loading the whole page would reset its restart baseline
        // and overwrite unrelated staged network or sampling requests.
        bool wasLoadingSettings = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            RebindPreference(StartupConflictCheckToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.StartupConflictCheckEnabled));
            RebindPreference(ShowStartupGuideToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.ShowStartupGuideOnStartup));
            RebindPreference(CheckStaleProxyToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.CheckStaleProxyOnStartup));
            RebindPreference(RestoreProxyOnExitToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.RestoreProxyOnExit));
            RebindPreference(NotificationEnabledToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.NotificationEnabled));
            RebindPreference(TrayUseMonochromeInactiveIconToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.TrayUseMonochromeInactiveIcon));
            RebindPreference(MainlandChinaUrlBlockingToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.MainlandChinaUrlBlockingEnabled));
            RebindPreference(TriggersEnabledToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.TriggersEnabled));
            RebindPreference(TriggerNotificationsEnabledToggle, ToggleSwitch.IsOnProperty, nameof(SettingsViewModel.TriggerNotificationsEnabled));
            RebindPreference(LanguageBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.DisplayLanguageIndex));
            RebindPreference(AppThemeModeBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.AppThemeModeIndex));
            RebindPreference(DataUnitStyleBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.DataUnitStyleIndex));
            RebindPreference(RateUnitBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.RateUnitIndex));
            RebindPreference(ClockFormatBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.ClockFormatIndex));
            RebindPreference(AppAccentColorModeBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.AppAccentColorModeIndex));
            RebindPreference(CloseBehaviorModeBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.CloseBehaviorModeIndex));
            RebindPreference(StartupBehaviorModeBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.StartupBehaviorModeIndex));
            RebindPreference(NotificationLevelBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.NotificationLevelIndex));
            RebindPreference(MainlandChinaFeatureModeBox, ComboBox.SelectedIndexProperty, nameof(SettingsViewModel.MainlandChinaFeatureModeIndex));
        }
        finally { _isLoadingSettings = wasLoadingSettings; }
    }

    private static void RebindPreference(FrameworkElement control, DependencyProperty property, string sourceProperty)
    {
        control.ClearValue(property);
        control.SetBinding(property, new Binding
        {
            Path = new PropertyPath(sourceProperty),
            Mode = BindingMode.OneWay,
        });
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
        await RunPageOperationAsync(async cancellationToken =>
        {
            ConnectionTestReport report = await _viewModel.RunConnectionTestAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ShowConnectionTestResultAsync(report, cancellationToken);
        });
    }

    /// <summary>Shows the connection test result.</summary>
    private async Task ShowConnectionTestResultAsync(
        ConnectionTestReport report,
        CancellationToken cancellationToken)
    {
        ConnectionTestDisplay display = new(report, _getString);
        List<Action> refreshText = [];
        await CenteredDialogOverlay.ShowAsync(
            GetDialogXamlRoot(),
            _getString("Settings.ConnectionTest.Result.Title"),
            BuildConnectionTestResultPanel(display, refreshText),
            _getString("Command.Close"),
            720,
            cancellationToken,
            dialog => dialog.Localize(() =>
            {
                dialog.Title = _getString("Settings.ConnectionTest.Result.Title");
                dialog.CloseButtonText = _getString("Command.Close");
                foreach (Action refresh in refreshText) { refresh(); }
                display.RefreshLocalization();
            }));
    }

    private StackPanel BuildConnectionTestResultPanel(ConnectionTestDisplay report, List<Action> refreshText)
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
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddConnectionTestHeader(table, refreshText);

        foreach (ConnectionTestTargetDisplay result in report.Rows)
        {
            AddConnectionTestResultRow(table, result);
        }

        panel.Children.Add(table);
        TextBlock summary = new()
        {
            Style = GetConnectionTestSummaryStyle(report.SummaryState),
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        summary.SetBinding(TextBlock.TextProperty, new Binding { Source = report, Path = new PropertyPath(nameof(ConnectionTestDisplay.SummaryText)) });
        panel.Children.Add(summary);
        return panel;
    }

    private void AddConnectionTestHeader(Grid table, List<Action> refreshText)
    {
        int rowIndex = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock url = AddConnectionTestText(table, rowIndex, 0, "URL", "ClashSecondaryCaptionTextBlockStyle");
        url.FlowDirection = FlowDirection.LeftToRight;
        url.TextAlignment = TextAlignment.Left;
        TextBlock status = AddConnectionTestText(table, rowIndex, 1, _viewModel.ConnectionTestStatusColumnText, "ClashSecondaryCaptionTextBlockStyle");
        TextBlock latency = AddConnectionTestText(table, rowIndex, 2, _viewModel.ConnectionTestLatencyColumnText, "ClashSecondaryCaptionTextBlockStyle");
        status.TextWrapping = TextWrapping.WrapWholeWords;
        refreshText.Add(() =>
        {
            status.Text = _viewModel.ConnectionTestStatusColumnText;
            latency.Text = _viewModel.ConnectionTestLatencyColumnText;
        });
    }

    private static void AddConnectionTestResultRow(Grid table, ConnectionTestTargetDisplay result)
    {
        int rowIndex = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock url = AddConnectionTestText(table, rowIndex, 0, result.Url, "BodyTextBlockStyle");
        url.FlowDirection = FlowDirection.LeftToRight;
        url.TextAlignment = TextAlignment.Left;
        ToolTipService.SetToolTip(url, result.Url);

        Grid statusPanel = new()
        {
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusPanel.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources[result.Succeeded
                ? "ClashSuccessIndicatorStyle"
                : "ClashCriticalIndicatorStyle"],
        });
        TextBlock status = new()
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        status.SetBinding(TextBlock.TextProperty, new Binding { Source = result, Path = new PropertyPath(nameof(ConnectionTestTargetDisplay.StatusText)) });
        status.SetBinding(ToolTipService.ToolTipProperty, new Binding { Source = result, Path = new PropertyPath(nameof(ConnectionTestTargetDisplay.StatusText)) });
        Grid.SetColumn(status, 1);
        statusPanel.Children.Add(status);
        Grid.SetRow(statusPanel, rowIndex);
        Grid.SetColumn(statusPanel, 1);
        table.Children.Add(statusPanel);

        TextBlock latency = AddConnectionTestText(table, rowIndex, 2, result.LatencyText, "BodyTextBlockStyle");
        latency.FlowDirection = FlowDirection.LeftToRight;
        latency.TextAlignment = TextAlignment.Right;
        latency.SetBinding(TextBlock.TextProperty, new Binding { Source = result, Path = new PropertyPath(nameof(ConnectionTestTargetDisplay.LatencyText)) });
    }

    /// <summary>Keeps status colors as theme references until the dialog joins its actual visual tree.</summary>
    private static Style GetConnectionTestSummaryStyle(ConnectionTestSummaryState summaryState)
    {
        string styleKey = summaryState switch
        {
            ConnectionTestSummaryState.AllPassed => "ClashSuccessTextBlockStyle",
            ConnectionTestSummaryState.PartialFailed => "ClashCautionTextBlockStyle",
            ConnectionTestSummaryState.AllFailed => "ClashCriticalTextBlockStyle",
            _ => "BodyStrongTextBlockStyle",
        };
        return (Style)Application.Current.Resources[styleKey];
    }

    private static TextBlock AddConnectionTestText(Grid table, int rowIndex, int columnIndex, string text, string styleKey)
    {
        TextBlock textBlock = new()
        {
            Text = text,
            Style = (Style)Application.Current.Resources[styleKey],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(textBlock, rowIndex);
        Grid.SetColumn(textBlock, columnIndex);
        table.Children.Add(textBlock);
        return textBlock;
    }

    /// <summary>Opens the Windows-native network repair dialog.</summary>
    /// <param name="sender">Clicked button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void OpenNetworkRepairButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            PageOperationSession diagnostics = new(_errorSink, "settings-diagnostic-dialog");
            List<Action> refreshText = [];
            try
            {
                await CenteredDialogOverlay.ShowAsync(
                    GetDialogXamlRoot(), _viewModel.WindowsNativeTitleText,
                    BuildNetworkRepairPanel(diagnostics, refreshText, cancellationToken),
                    _getString("Command.Close"), 688, cancellationToken,
                    dialog => dialog.Localize(() =>
                    {
                        dialog.Title = _viewModel.WindowsNativeTitleText;
                        dialog.CloseButtonText = _getString("Command.Close");
                        foreach (Action refresh in refreshText) { refresh(); }
                        _viewModel.RefreshDiagnosticStatusPresentation();
                    }));
            }
            finally
            {
                diagnostics.Cancel();
                await diagnostics.DrainAsync();
            }
        });
    }

    /// <summary>Opens the application accent color picker and persists the selected color.</summary>
    /// <param name="sender">Clicked color swatch button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AppAccentColorSwatchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            ColorPicker picker = new()
            {
                Color = _parseAccentColor(_viewModel.AppAccentColorValue),
                IsAlphaEnabled = false,
                IsMoreButtonVisible = false,
                Width = 320,
                MaxWidth = 320,
            };
            using ColorPickerLocalization pickerLabels = new(picker, _getString, _errorSink);

            Grid pickerPanel = new()
            {
                Width = 340,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pickerPanel.Children.Add(picker);

            XamlRoot xamlRoot = GetDialogXamlRoot();
            ScrollViewer pickerScroll = new()
            {
                Content = pickerPanel,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = Math.Max(160, xamlRoot.Size.Height - 200),
            };
            AutomationProperties.SetAutomationId(pickerScroll, "AccentColorScrollViewer");
            // Keep room for the native title and action row. An unconstrained child measure
            // lets the picker expose all channels instead of clipping its final input rows.
            void UpdatePickerViewport(XamlRoot sender, XamlRootChangedEventArgs args) =>
                pickerScroll.MaxHeight = Math.Max(160, sender.Size.Height - 200);

            ThemedContentDialog dialog = new()
            {
                Title = _viewModel.AppAccentColorTitleText,
                Content = pickerScroll,
                PrimaryButtonText = _viewModel.AppAccentColorPickText,
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 420d;
            dialog.Localize(() =>
            {
                dialog.Title = _viewModel.AppAccentColorTitleText;
                dialog.PrimaryButtonText = _viewModel.AppAccentColorPickText;
                dialog.CloseButtonText = _getString("Command.Cancel");
                pickerLabels.RefreshLocalization();
            });

            ContentDialogResult result;
            xamlRoot.Changed += UpdatePickerViewport;
            try { result = await dialog.ShowManagedAsync(cancellationToken); }
            finally { xamlRoot.Changed -= UpdatePickerViewport; }
            if (result is not ContentDialogResult.Primary)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _viewModel.ApplyCustomAccentColorAsync(_formatAccentColor(picker.Color), cancellationToken);
        });
    }

    /// <summary>Opens the connection-test URL editor dialog.</summary>
    private async void EditConnectionTestUrlsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            TextBox proxyUrl1Box = new() { Text = _viewModel.ConnectionTestProxyUrl1, MinWidth = 360 };
            TextBox proxyUrl2Box = new() { Text = _viewModel.ConnectionTestProxyUrl2, MinWidth = 360 };
            TextBox directUrlBox = new() { Text = _viewModel.ConnectionTestDirectUrl, MinWidth = 360 };
            InfoBar validationError = new()
            {
                Severity = InfoBarSeverity.Error,
                IsClosable = false,
                Message = _viewModel.ConnectionTestUrlValidationText,
            };
            ThemedContentDialog dialog = new()
            {
                Title = _viewModel.ConnectionTestUrlTitleText,
                PrimaryButtonText = _getString("Command.Save"),
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = GetDialogXamlRoot(),
            };
            dialog.Content = BuildConnectionTestUrlsPanel(
                dialog, proxyUrl1Box, proxyUrl2Box, directUrlBox, validationError);
            dialog.Localize(() =>
            {
                dialog.Title = _viewModel.ConnectionTestUrlTitleText;
                dialog.PrimaryButtonText = _getString("Command.Save");
                dialog.CloseButtonText = _getString("Command.Cancel");
                validationError.Message = _viewModel.ConnectionTestUrlValidationText;
            });

            void ValidateDraft(ContentDialog sender, ContentDialogButtonClickEventArgs args)
            {
                int invalidIndex = SettingsViewModel.GetInvalidConnectionTestUrlIndex(
                    proxyUrl1Box.Text, proxyUrl2Box.Text, directUrlBox.Text);
                args.Cancel = invalidIndex >= 0;
                validationError.IsOpen = args.Cancel;
                if (invalidIndex >= 0)
                {
                    TextBox invalidField = invalidIndex switch
                    {
                        0 => proxyUrl1Box,
                        1 => proxyUrl2Box,
                        _ => directUrlBox,
                    };
                    invalidField.Focus(FocusState.Programmatic);
                }
            }

            dialog.PrimaryButtonClick += ValidateDraft;
            try
            {
                ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result is ContentDialogResult.Primary)
                {
                    await _viewModel.ApplyConnectionTestUrlsAsync(proxyUrl1Box.Text, proxyUrl2Box.Text, directUrlBox.Text, cancellationToken);
                }
            }
            finally
            {
                dialog.PrimaryButtonClick -= ValidateDraft;
            }
        });
    }

    /// <summary>Builds the connection-test URL editor content.</summary>
    private StackPanel BuildConnectionTestUrlsPanel(
        ThemedContentDialog dialog,
        TextBox proxyUrl1Box,
        TextBox proxyUrl2Box,
        TextBox directUrlBox,
        InfoBar validationError)
    {
        StackPanel panel = new()
        {
            Spacing = 10,
            MinWidth = 420,
        };
        AddConnectionTestUrlEditorRow(panel, dialog, () => _viewModel.ConnectionTestProxyUrl1TitleText, proxyUrl1Box);
        AddConnectionTestUrlEditorRow(panel, dialog, () => _viewModel.ConnectionTestProxyUrl2TitleText, proxyUrl2Box);
        AddConnectionTestUrlEditorRow(panel, dialog, () => _viewModel.ConnectionTestDirectUrlTitleText, directUrlBox);
        panel.Children.Add(validationError);

        HyperlinkButton restoreButton = new()
        {
            Name = "RestoreConnectionTestUrlsButton",
            Content = _viewModel.ResetText,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        dialog.Localize(() => restoreButton.Content = _viewModel.ResetText);
        restoreButton.Click += (_, _) =>
        {
            proxyUrl1Box.Text = SettingsViewModel.DefaultConnectionTestProxyUrl1;
            proxyUrl2Box.Text = SettingsViewModel.DefaultConnectionTestProxyUrl2;
            directUrlBox.Text = SettingsViewModel.DefaultConnectionTestDirectUrl;
            validationError.IsOpen = false;
        };
        panel.Children.Add(restoreButton);
        return panel;
    }

    private static void AddConnectionTestUrlEditorRow(StackPanel panel, ThemedContentDialog dialog, Func<string> getLabel, TextBox textBox)
    {
        TextBlock label = new()
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        dialog.Localize(() =>
        {
            label.Text = getLabel();
            AutomationProperties.SetName(textBox, label.Text);
        });
        panel.Children.Add(label);
        panel.Children.Add(textBox);
    }

    private async void ResetBasicSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(token => _viewModel.ResetPreferenceGroupAsync(SettingsResetScope.Basic, token));
    }

    private async void ResetStartupSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetStartupSettingsToDefaultsAsync);
    }

    private async void ResetNotificationSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(token => _viewModel.ResetPreferenceGroupAsync(SettingsResetScope.Notifications, token));
    }

    private async void ResetTriggerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(token => _viewModel.ResetPreferenceGroupAsync(SettingsResetScope.Triggers, token));
    }

    private async void ResetTraySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(token => _viewModel.ResetPreferenceGroupAsync(SettingsResetScope.Tray, token));
    }

    private async void ResetTransparentProxySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetTransparentProxySettingsToDefaultsAsync, includeServiceDeploymentNote: true);
    }

    private async void ResetProxySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(_viewModel.ResetProxySettingsToDefaultsAsync, includeServiceDeploymentNote: true);
    }

    private async void ResetWindowsNativeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(token => _viewModel.ResetPreferenceGroupAsync(SettingsResetScope.WindowsNative, token), includeServiceDeploymentNote: true);
    }

    private async void ResetMainlandChinaSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ResetSettingsGroupAsync(token => _viewModel.ResetPreferenceGroupAsync(SettingsResetScope.MainlandChina, token));
    }

    /// <summary>Owns an asynchronous group reset until its activation or compensation has drained.</summary>
    private Task ResetSettingsGroupAsync(
        Func<CancellationToken, Task> resetAction,
        bool includeServiceDeploymentNote = false)
    {
        return RunPageOperationAsync(token => ResetSettingsGroupCoreAsync(resetAction, includeServiceDeploymentNote, token));
    }

    private async Task ResetSettingsGroupCoreAsync(Func<CancellationToken, Task> resetAction, bool includeServiceDeploymentNote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resetAction);
        string message = _viewModel.ResetGroupConfirmMessageText;
        if (includeServiceDeploymentNote)
        {
            message = $"{message}{Environment.NewLine}{Environment.NewLine}{_viewModel.ResetGroupServiceDeploymentNoteText}";
        }

        ThemedContentDialog dialog = new()
        {
            Title = _viewModel.ResetGroupConfirmTitleText,
            Content = message,
            PrimaryButtonText = _viewModel.ResetGroupToDefaultsText,
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };
        dialog.Localize(() =>
        {
            dialog.Title = _viewModel.ResetGroupConfirmTitleText;
            dialog.Content = includeServiceDeploymentNote
                ? $"{_viewModel.ResetGroupConfirmMessageText}{Environment.NewLine}{Environment.NewLine}{_viewModel.ResetGroupServiceDeploymentNoteText}"
                : _viewModel.ResetGroupConfirmMessageText;
            dialog.PrimaryButtonText = _viewModel.ResetGroupToDefaultsText;
            dialog.CloseButtonText = _getString("Command.Cancel");
        });

        if (await dialog.ShowManagedAsync(cancellationToken) is ContentDialogResult.Primary)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await resetAction(cancellationToken);
            RefreshPreferenceControls();
        }
    }

    /// <summary>Opens the searchable tray feature selector.</summary>
    private async void EditTrayVisibleFeaturesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            HashSet<string> selectedIds = new(
                _viewModel.TrayVisibleFeatureIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            SearchableOptionList optionList = new()
            {
                SearchHeader = _getString("Settings.Tray.VisibleFeatures.SelectionRequired"),
                SearchPlaceholder = _viewModel.TrayVisibleFeatureSearchPlaceholderText,
                EmptyText = _getString("Common.NoMatchingOptions"),
                AllowMultiple = true,
                AllowReorder = true,
                MoveUpText = _getString("Command.MoveUp"),
                MoveDownText = _getString("Command.MoveDown"),
                ReorderHint = _getString("Settings.Tray.VisibleFeatures.ReorderHint"),
                MaxListHeight = 360,
            };
            IEnumerable<SettingsTrayFeatureDefinition> orderedFeatures = _viewModel.GetTrayVisibleFeatureDefinitions()
                .Concat(SettingsViewModel.TrayFeatureDefinitions)
                .DistinctBy(static feature => feature.Id, StringComparer.OrdinalIgnoreCase);
            optionList.SetOptions(orderedFeatures.Select(feature => new SearchableOptionItem(
                feature.Id,
                _getString(feature.TitleKey),
                string.Empty,
                _getString(feature.DescriptionKey),
                feature.Glyph,
                feature.Id,
                selectedIds.Contains(feature.Id))));

            ThemedContentDialog dialog = new()
            {
                Title = _viewModel.TrayVisibleFeaturesTitleText,
                Content = optionList,
                IsPrimaryButtonEnabled = optionList.SelectedOptions.Count > 0,
                PrimaryButtonText = _getString("Command.Save"),
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = GetDialogXamlRoot(),
            };

            optionList.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = optionList.SelectedOptions.Count > 0;
            using SearchableOptionDialogLayout layout = new(dialog, optionList);
            dialog.Localize(() =>
            {
                dialog.Title = _viewModel.TrayVisibleFeaturesTitleText;
                dialog.PrimaryButtonText = _getString("Command.Save");
                dialog.CloseButtonText = _getString("Command.Cancel");
                optionList.SearchHeader = _getString("Settings.Tray.VisibleFeatures.SelectionRequired");
                optionList.SearchPlaceholder = _viewModel.TrayVisibleFeatureSearchPlaceholderText;
                optionList.EmptyText = _getString("Common.NoMatchingOptions");
                optionList.MoveUpText = _getString("Command.MoveUp");
                optionList.MoveDownText = _getString("Command.MoveDown");
                optionList.ReorderHint = _getString("Settings.Tray.VisibleFeatures.ReorderHint");
                foreach (SearchableOptionItem option in optionList.Options)
                {
                    SettingsTrayFeatureDefinition feature = SettingsViewModel.TrayFeatureDefinitions.First(item => item.Id == option.Id);
                    option.UpdateDisplay(_getString(feature.TitleKey), string.Empty, _getString(feature.DescriptionKey));
                }
                optionList.RefreshFilter();
                layout.RefreshLayout();
            });
            if (await dialog.ShowManagedAsync(cancellationToken) is ContentDialogResult.Primary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _viewModel.ApplyTrayVisibleFeatureIdsAsync(optionList.SelectedOptions.Select(static option => option.Id), cancellationToken);
            }
        });
    }

    /// <summary>Runs startup conflict detection immediately and shows the shared result dialog.</summary>
    /// <param name="sender">Clicked button. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void CheckStartupConflictsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            IReadOnlyList<StartupConflictIssue> issues = await _viewModel.CheckStartupConflictsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await StartupConflictDialogPresenter.ShowAsync(
                GetDialogXamlRoot(), issues, _getString, _errorSink, cancellationToken);
        });
    }

    /// <summary>Shows the startup prompt immediately.</summary>
    private async void ShowStartupPromptButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            await _startupGuide.ShowAsync(GetDialogXamlRoot(), cancellationToken);
        });
    }

    /// <summary>Registers the startup restore fallback helper.</summary>
    private async void RegisterStartupRestoreFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(_viewModel.RegisterStartupRestoreFallbackAsync);
    }

    /// <summary>Refreshes startup restore fallback registration status.</summary>
    private async void DetectStartupRestoreFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(_viewModel.RefreshStartupRestoreFallbackStatusAsync);
    }

    /// <summary>Removes the startup restore fallback registration.</summary>
    private async void RemoveStartupRestoreFallbackRegistrationButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(_viewModel.RemoveStartupRestoreFallbackRegistrationAsync);
    }

    /// <summary>Exports settings through the shared, page-owned backup workflow.</summary>
    private async void ExportDataPackageButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(token => _dataPackages.ExportAsync(GetDialogXamlRoot(), token));
    }

    /// <summary>Imports settings through the shared transaction and reloads only after successful completion.</summary>
    private async void ImportDataPackageButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async token =>
        {
            await _dataPackages.ImportAsync(GetDialogXamlRoot(), async refreshToken =>
            {
                refreshToken.ThrowIfCancellationRequested();
                _viewModel.ReloadAfterDataImport();
                RefreshPreferenceControls();
                await _viewModel.RefreshStartupRestoreFallbackStatusAsync(refreshToken);
            }, token);
        });
    }

    private Task RunPageOperationAsync(Func<CancellationToken, Task> operation)
    {
        return !_isLoaded
            ? Task.CompletedTask
            : _pageOperations.RunAsync(async token =>
            {
                Control? focusedControl = GetPageFocusedControl();
                FocusState focusState = focusedControl?.FocusState ?? FocusState.Programmatic;
                IsEnabled = false;
                try
                {
                    await operation(token);
                }
                finally
                {
                    IsEnabled = true;
                    if (_isLoaded && !token.IsCancellationRequested
                        && focusedControl is { IsEnabled: true, Visibility: Visibility.Visible }
                        && focusedControl.XamlRoot == XamlRoot)
                    {
                        focusedControl.Focus(focusState);
                    }
                }
            });
    }

    private Control? GetPageFocusedControl()
    {
        Control? focusedControl = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        for (DependencyObject? element = focusedControl; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, this))
            {
                return focusedControl;
            }
        }

        return null;
    }

    /// <summary>Builds the network repair dialog content.</summary>
    /// <returns>Dialog content panel.</returns>
    private ScrollViewer BuildNetworkRepairPanel(PageOperationSession diagnostics, List<Action> refreshText, CancellationToken cancellationToken)
    {
        StackPanel panel = new()
        {
            Spacing = 8,
            MinWidth = 360,
            MaxWidth = 640,
        };

        AddDiagnosticRow(panel, () => _viewModel.WslDiagnosticTitleText, nameof(SettingsViewModel.WslDiagnosticStatusText), "Wsl", diagnostics, refreshText, cancellationToken);
        AddDiagnosticRow(panel, () => _viewModel.TerminalDiagnosticTitleText, nameof(SettingsViewModel.TerminalDiagnosticStatusText), "Terminal", diagnostics, refreshText, cancellationToken);
        AddDiagnosticRow(panel, () => _viewModel.StoreDiagnosticTitleText, nameof(SettingsViewModel.StoreDiagnosticStatusText), "MicrosoftStore", diagnostics, refreshText, cancellationToken);

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
    /// <param name="getTitle">Resolves the captured target's display title.</param>
    /// <param name="statusPropertyName">Bindable status property name. Must not be null.</param>
    /// <param name="targetTag">Diagnostic target tag. Must not be null.</param>
    /// <param name="diagnostics">Owns the diagnostic operations accepted by this dialog.</param>
    /// <param name="refreshText">Retains display-only updates for the dialog lifetime.</param>
    /// <param name="cancellationToken">Cancels work when the page leaves the visual tree.</param>
    private void AddDiagnosticRow(
        StackPanel panel,
        Func<string> getTitle,
        string statusPropertyName,
        string targetTag,
        PageOperationSession diagnostics,
        List<Action> refreshText,
        CancellationToken cancellationToken)
    {
        Grid row = new()
        {
            Style = (Style)Application.Current.Resources["ClashSectionRowStyle"],
            Padding = new Thickness(0, 12, 0, 12),
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
        TextBlock title = new()
        {
            Text = getTitle(),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        refreshText.Add(() => title.Text = getTitle());
        textPanel.Children.Add(title);

        TextBlock statusText = new()
        {
            Style = (Style)Application.Current.Resources["ClashSecondaryCaptionTextBlockStyle"],
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        statusText.SetBinding(TextBlock.TextProperty, new Binding { Source = _viewModel, Path = new PropertyPath(statusPropertyName) });
        textPanel.Children.Add(statusText);
        row.Children.Add(textPanel);

        Grid buttonPanel = new()
        {
            ColumnSpacing = 8,
        };
        for (int index = 0; index < 3; index++) { buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); }
        Grid.SetRow(buttonPanel, 1);
        AddDiagnosticButton(buttonPanel, 0, "\uE9D9", () => _viewModel.DiagnoseText, $"{targetTag}:Diagnose", diagnostics, refreshText, cancellationToken);
        AddDiagnosticButton(buttonPanel, 1, "\uE73E", () => _viewModel.ApplyText, $"{targetTag}:Apply", diagnostics, refreshText, cancellationToken);
        AddDiagnosticButton(buttonPanel, 2, "\uE72C", () => _viewModel.ResetText, $"{targetTag}:Reset", diagnostics, refreshText, cancellationToken);
        row.Children.Add(buttonPanel);

        panel.Children.Add(row);
    }

    /// <summary>Adds one command button to a diagnostic row.</summary>
    private void AddDiagnosticButton(
        Grid panel,
        int column,
        string glyph,
        Func<string> getText,
        string commandParameter,
        PageOperationSession diagnostics,
        List<Action> refreshText,
        CancellationToken cancellationToken)
    {
        Button button = new()
        {
            Name = "WindowsDiagnostic" + commandParameter.Replace(":", string.Empty, StringComparison.Ordinal) + "Button",
            Tag = commandParameter,
            Command = new AsyncRelayCommand(
                _ => diagnostics.RunAsync(
                    token => _viewModel.WindowsDiagnosticCommand.ExecuteAsync(commandParameter, token),
                    cancellationToken),
                _errorSink,
                operationName: "settings-diagnostic-command"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        Grid content = new()
        {
            ColumnSpacing = 6,
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        TextBlock label = new() { TextWrapping = TextWrapping.WrapWholeWords, TextAlignment = TextAlignment.Center };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        button.Content = content;
        Action refresh = () =>
        {
            label.Text = getText();
            AutomationProperties.SetName(button, label.Text);
            ToolTipService.SetToolTip(button, label.Text);
        };
        refresh();
        refreshText.Add(refresh);
        Grid.SetColumn(button, column);
        panel.Children.Add(button);
    }

    /// <summary>Retries the failed settings applications still identified by the page.</summary>
    private async void RetryApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SettingsApplicationIssue issue })
        {
            await RunPageOperationAsync(token => _recovery.RetryAsync(issue, token));
        }
    }

    /// <summary>Retries the failed settings applications still identified by the page.</summary>
    private async void RetryRuntimeSettingsReset_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(_viewModel.RetryRuntimeSettingsResetAsync);
    }

    /// <summary>Confirms the effect of restoring all settings before changing them.</summary>
    private async void ResetAllSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            if (!await ConfirmAsync(
                    _getString("Settings.ResetAllSettings.Title"),
                    _getString("Settings.ResetAllSettings.Confirm"),
                    _viewModel.ResetText, cancellationToken))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _viewModel.ResetAllSettingsAsync(cancellationToken);
            RefreshPreferenceControls();
        });
    }

    /// <summary>Confirms permanent data removal and the ensuing application restart.</summary>
    private async void ClearAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            if (!await ConfirmAsync(
                    _getString("Settings.ClearAllData.Title"),
                    _getString("Settings.ClearAllData.Confirm"),
                    _getString("Settings.ClearAllData.Action"), cancellationToken))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _viewModel.ClearAllDataAsync(cancellationToken);
        });
    }

    private async void McpApply_Click(object sender, RoutedEventArgs args) => await RunPageOperationAsync(_mcp.ApplyAsync);
    private async void McpRefresh_Click(object sender, RoutedEventArgs args) => await RunPageOperationAsync(_mcp.RefreshStatusAsync);
    private async void McpRotate_Click(object sender, RoutedEventArgs args) => await RunPageOperationAsync(_mcp.RotateAsync);
    private async void McpReset_Click(object sender, RoutedEventArgs args) => await RunPageOperationAsync(_mcp.ResetAsync);
    private async void McpCopy_Click(object sender, RoutedEventArgs args) => await RunPageOperationAsync(token =>
        _mcp.CopyAsync(McpClientClipboard.TryCopy, token));

    /// <summary>Shows a destructive-action confirmation dialog.</summary>
    private async Task<bool> ConfirmAsync(string title, string content, string primaryButtonText, CancellationToken cancellationToken)
    {
        ThemedContentDialog dialog = new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetDialogXamlRoot(),
        };

        return await dialog.ShowManagedAsync(cancellationToken) is ContentDialogResult.Primary;
    }

}
